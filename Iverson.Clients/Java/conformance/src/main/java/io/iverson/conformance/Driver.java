package io.iverson.conformance;

import com.google.gson.Gson;
import com.google.gson.JsonElement;
import com.google.gson.JsonParser;
import com.google.gson.reflect.TypeToken;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;
import io.grpc.StatusRuntimeException;
import io.iverson.client.core.EntityCoordinator;
import io.iverson.client.core.IversonClient;
import io.iverson.client.core.SchemaRegistrar;
import io.iverson.conformance.models.JavaArticle;
import io.iverson.conformance.models.JavaAuthor;
import io.iverson.conformance.models.JavaTag;
import io.iverson.conformance.models.ErrorDoc;
import io.iverson.conformance.models.ErrorUnregisteredDoc;
import io.iverson.conformance.models.IdentityDoc;
import io.iverson.conformance.models.QueryDoc;
import io.iverson.client.search.AggregateBuilder;
import io.iverson.client.search.Query;
import io.iverson.client.search.QueryBuilder;
import iverson.ObjectSearch;
import iverson.ObjectSearch.SearchOperator;
import io.iverson.conformance.models.SharedArticle;
import io.iverson.conformance.models.SharedAuthor;
import io.iverson.conformance.models.VectorDoc;
import io.iverson.client.search.ChunksBuilder;
import io.iverson.client.search.SimilarBuilder;
import iverson.ObjectMapping.SchemaField;
import iverson.ObjectMapping.SchemaRelation;
import iverson.ObjectMapping.SchemaType;

import java.lang.reflect.Type;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.TimeUnit;

/**
 * The Java conformance driver. Reports; never asserts — every judgement belongs to the
 * orchestrator's Verifier. A step that throws becomes ok:false with an error message and the
 * process still exits 0; a non-zero exit means the driver itself broke (bad flags, unsupported
 * scenario, unwritable --out).
 *
 * <p>Output goes only to {@code --out}: SLF4J's default console appender writes to stdout, which
 * would corrupt the JSON document if anything were printed there. Nothing in this driver's
 * happy path writes to stdout; failures are reported in the document, not on the console.
 */
public final class Driver {

    private static final String LANGUAGE = "java";
    private static final String CRUD_ROUNDTRIP_SCENARIO = "crud-roundtrip";
    // interop (S4) is register-phase-NEVER for this driver: only .NET registers
    // SharedAuthor/SharedArticle (register-once rule) — see doRegister, which never handles it.
    private static final String INTEROP_SCENARIO = "interop";
    // schema-catalog (S5) uses only the register and read phases: this driver registers JavaAuthor
    // and then fetches the catalogue back through IversonClient.getSchema.
    private static final String SCHEMA_CATALOG_SCENARIO = "schema-catalog";
    // query (S6) is register-phase-NEVER for this driver: only .NET registers QueryDoc
    // (register-once rule). This driver seeds one row and then issues a filtered search and a
    // count aggregate through the client library's own QueryBuilder/AggregateBuilder.
    private static final String QUERY_SCENARIO = "query";
    // vector-search (S7) is register-phase-NEVER for this driver: only .NET registers VectorDoc
    // (register-once rule). This driver seeds one row and then issues a SearchSimilar and a
    // SearchChunks through the client library's own vector-search builders.
    private static final String VECTOR_SEARCH_SCENARIO = "vector-search";
    // identity (S8) is register-phase-NEVER for this driver: only .NET registers IdentityDoc
    // (register-once rule). This driver creates one row under its own acting user, reads it back,
    // and then attempts ONE update carrying --wrong-acting-token — an acting user belonging to a
    // different tenant — reporting the gRPC status code that attempt received.
    private static final String IDENTITY_SCENARIO = "identity";
    // error-contract (S9) is register-phase-NEVER for this driver: only .NET registers ErrorDoc
    // (register-once rule). This driver seeds one row, reads it back as a positive control, reads a
    // key no row exists under, and attempts one mapped write against ErrorUnregisteredDoc — a type
    // nothing ever registers — reporting the gRPC status code and detail each received.
    private static final String ERROR_CONTRACT_SCENARIO = "error-contract";
    private static final java.util.Set<String> SUPPORTED_SCENARIOS =
        java.util.Set.of(CRUD_ROUNDTRIP_SCENARIO, INTEROP_SCENARIO, SCHEMA_CATALOG_SCENARIO,
            QUERY_SCENARIO, VECTOR_SEARCH_SCENARIO, IDENTITY_SCENARIO, ERROR_CONTRACT_SCENARIO);

    /**
     * The tenant value every driver stamps on the IdentityDoc row it creates: deliberately NOT the
     * acting user's tenant. The server force-sets the tenant column from the acting-user token, so
     * the read-back must show the acting tenant instead — an assertion that would agree by
     * construction if the driver sent the right value here. Must stay in step with
     * {@code IdentityScenario.WrongTenantValue}.
     */
    private static final String IDENTITY_WRONG_TENANT = "tenant_not_the_acting_user";

    /**
     * The Label every VectorDoc row this driver writes carries, and the value the orchestrator's
     * similarity comparison grades on. Must stay in step with {@code VectorSearchScenario.LabelFor}.
     */
    private static final String VECTOR_DOC_LABEL = "vec-" + LANGUAGE;
    /**
     * Shared verbatim by all five drivers: a per-language query text would make a disagreement
     * between two cells un-attributable to the client libraries, and a top-k below the seeded row
     * count would turn the orchestrator's exact set comparisons into prefix comparisons.
     */
    private static final String VECTOR_QUERY_TEXT = "a short note about vector search conformance";
    private static final int VECTOR_TOP_K = 50;
    private static final Gson GSON = new Gson();

    private Driver() {}

    public static void main(String[] args) throws Exception {
        Args parsedArgs = Args.parse(args);

        String scenario = parsedArgs.require("--scenario");
        if (!SUPPORTED_SCENARIOS.contains(scenario)) {
            System.err.println(
                "unsupported scenario '" + scenario + "'; this driver implements " + SUPPORTED_SCENARIOS);
            System.exit(2);
            return;
        }

        String phase = parsedArgs.require("--phase");
        String tenant = parsedArgs.require("--tenant");
        String ownerId = parsedArgs.require("--owner-id");
        String idPrefix = parsedArgs.require("--id-prefix");
        String outPath = parsedArgs.require("--out");
        String typeHint = parsedArgs.optional("--type");

        URI grpcUri = URI.create(parsedArgs.require("--grpc"));
        Map<String, String> priorKeys = Keys.parse(parsedArgs.optional("--keys"), LANGUAGE);

        // The capture seam wraps the whole channel (SchemaRegistrar reads the package-private
        // IversonClient.mappingStub, so there is no stub to wrap directly); both identities ride
        // as CallCredentials because EntityCoordinator's mapped CRUD takes no header parameter.
        CaptureInterceptor capture = new CaptureInterceptor();
        DualHeaderCredentials credentials = new DualHeaderCredentials(
            parsedArgs.optional("--client-id"),
            parsedArgs.optional("--client-secret"),
            parsedArgs.optional("--token-endpoint"),
            parsedArgs.optional("--acting-token"),
            parsedArgs.optional("--service-token"));

        ManagedChannel channel = ManagedChannelBuilder.forAddress(grpcUri.getHost(), grpcUri.getPort())
            .usePlaintext()
            .intercept(capture)
            .build();

        List<StepResult> steps = new ArrayList<>();
        try {
            IversonClient client = new IversonClient(channel, credentials);

            if (INTEROP_SCENARIO.equals(scenario)) {
                switch (phase) {
                    case "write" -> doInteropWrite(client, tenant, ownerId, idPrefix, steps);
                    case "read" -> doInteropRead(client, parsedArgs.optional("--keys"), steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "' for scenario '" + scenario + "'");
                        System.exit(2);
                        return;
                    }
                }
            } else if (QUERY_SCENARIO.equals(scenario)) {
                switch (phase) {
                    case "write" -> doQueryWrite(client, tenant, ownerId, idPrefix, steps);
                    case "read" -> doQueryRead(client, idPrefix, steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "' for scenario '" + scenario + "'");
                        System.exit(2);
                        return;
                    }
                }
            } else if (VECTOR_SEARCH_SCENARIO.equals(scenario)) {
                switch (phase) {
                    case "write" -> doVectorSearchWrite(client, tenant, ownerId, idPrefix, steps);
                    case "read" -> doVectorSearchRead(client, idPrefix, steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "' for scenario '" + scenario + "'");
                        System.exit(2);
                        return;
                    }
                }
            } else if (IDENTITY_SCENARIO.equals(scenario)) {
                switch (phase) {
                    case "write" -> doIdentityWrite(client, ownerId, idPrefix, steps);
                    case "read" -> doIdentityRead(
                        client, channel, parsedArgs, tenant, ownerId, idPrefix, priorKeys, steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "' for scenario '" + scenario + "'");
                        System.exit(2);
                        return;
                    }
                }
            } else if (ERROR_CONTRACT_SCENARIO.equals(scenario)) {
                switch (phase) {
                    case "write" -> doErrorContractWrite(client, tenant, ownerId, idPrefix, steps);
                    case "read" -> doErrorContractRead(client, tenant, ownerId, idPrefix, priorKeys, steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "' for scenario '" + scenario + "'");
                        System.exit(2);
                        return;
                    }
                }
            } else if (SCHEMA_CATALOG_SCENARIO.equals(scenario)) {
                switch (phase) {
                    case "register" -> doSchemaCatalogRegister(client, capture, steps);
                    case "read" -> doSchemaCatalogRead(client, steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "' for scenario '" + scenario + "'");
                        System.exit(2);
                        return;
                    }
                }
            } else {
                switch (phase) {
                    case "register" -> doRegister(client, capture, typeHint, steps);
                    case "write" -> doWrite(client, tenant, ownerId, idPrefix, steps);
                    case "read" -> doRead(client, idPrefix, priorKeys, steps);
                    case "update" -> doUpdate(client, tenant, ownerId, idPrefix, priorKeys, steps);
                    case "delete" -> doDelete(client, idPrefix, priorKeys, steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "'");
                        System.exit(2);
                        return;
                    }
                }
            }
        } finally {
            channel.shutdown().awaitTermination(5, TimeUnit.SECONDS);
        }

        PhaseDocument document = new PhaseDocument(LANGUAGE, phase, steps);
        Path out = Path.of(outPath);
        if (out.getParent() != null) Files.createDirectories(out.getParent());
        Files.writeString(out, GSON.toJson(document), StandardCharsets.UTF_8);
    }

    // ── register ─────────────────────────────────────────────────────────────────────────────

    /**
     * One step per registered type. Every type the orchestrator has to re-register with
     * authorization rules needs its own descriptor reported: a type whose stored schema has no
     * Authorization block is writable by nobody.
     *
     * <p>{@code SchemaRegistrar.registerAll} issues one {@code RegisterSchema} call per type,
     * sequentially, and throws on the first validation failure (RegisterSchema has no
     * Success=false path) — so the sequence aborts at the first failing type. All three steps
     * therefore share that aborted sequence's outcome; {@code typeDescriptor} presence is what
     * says which types were actually sent.
     */
    private static void doRegister(
            IversonClient client, CaptureInterceptor capture, String typeHint, List<StepResult> steps) {
        String registerError = null;
        try {
            SchemaRegistrar registrar = new SchemaRegistrar(client);
            registrar.registerAll(JavaAuthor.class, JavaTag.class, JavaArticle.class);
        } catch (Exception e) {
            registerError = describe(e);
        }

        steps.add(registerStep("register", registerError, capture.select(typeHint, "JavaArticle")));
        steps.add(registerStep("register_author", registerError, capture.select("JavaAuthor")));
        steps.add(registerStep("register_tag", registerError, capture.select("JavaTag")));
    }

    private static StepResult registerStep(String name, String error, String descriptorJson) {
        StepResult result = new StepResult(name);
        result.ok = error == null;
        result.error = error;
        result.typeDescriptor = descriptorJson == null ? null : JsonParser.parseString(descriptorJson);
        return result;
    }

    // ── S5 schema-catalog ────────────────────────────────────────────────────────────────────

    /**
     * S5 schema-catalog: one relation-free type, registered WITHOUT an authorization block on
     * purpose — the orchestrator re-registers it with one before the read phase, and until it does
     * the type is Denied for Read and GetSchema omits it entirely. JavaAuthor is this language's
     * own type name, so all five languages registering concurrently overwrite nothing.
     */
    private static void doSchemaCatalogRegister(
            IversonClient client, CaptureInterceptor capture, List<StepResult> steps) {
        String registerError = null;
        try {
            SchemaRegistrar registrar = new SchemaRegistrar(client);
            registrar.registerAll(JavaAuthor.class);
        } catch (Exception e) {
            registerError = describe(e);
        }

        steps.add(registerStep("register_schema_type", registerError, capture.select("JavaAuthor")));
    }

    /**
     * Fetches the catalogue through the client library's own public {@code getSchema} and reports
     * what came back verbatim. Nothing here judges; the orchestrator does.
     */
    private static void doSchemaCatalogRead(IversonClient client, List<StepResult> steps) {
        StepResult result = new StepResult("get_schema");
        try {
            result.entity = GSON.toJsonTree(catalogueToReport(client.getSchema("", null)));
        } catch (Exception e) {
            result.ok = false;
            result.error = describe(e);
        }
        steps.add(result);
    }

    /**
     * The deliberately minimal, cross-language-identical projection of a GetSchema catalogue that
     * all five drivers report. Copies names verbatim out of the SchemaType messages the client
     * library returned; filters nothing and decides nothing.
     */
    private static Map<String, Object> catalogueToReport(List<SchemaType> types) {
        List<Object> reported = new ArrayList<>();
        for (SchemaType type : types) {
            List<Object> fields = new ArrayList<>();
            for (SchemaField field : type.getFieldsList()) {
                fields.add(java.util.Map.of("name", field.getName()));
            }
            List<Object> relations = new ArrayList<>();
            for (SchemaRelation relation : type.getRelationsList()) {
                relations.add(java.util.Map.of("propertyName", relation.getPropertyName()));
            }
            reported.add(java.util.Map.of(
                "name", type.getName(), "fields", fields, "relations", relations));
        }
        return java.util.Map.of("types", reported);
    }

    // ── S6 query ─────────────────────────────────────────────────────────────────────────────

    /**
     * Seeds one {@code QueryDoc} row stamped with the run's marker. The key is reported whenever
     * {@code persist} returned one — it is the orchestrator's expected-set accounting, and a row
     * seeded but never reported would silently shrink what every language is graded against.
     */
    private static void doQueryWrite(
            IversonClient client, String tenant, String ownerId, String idPrefix, List<StepResult> steps) {
        String[] docKey = new String[1];
        StepResult result = step("write_query_doc", r -> {
            QueryDoc doc = new QueryDoc();
            doc.setTenantId(tenant);
            doc.setOwnerId(ownerId);
            doc.setMarker(idPrefix);
            doc.setLabel("doc-" + LANGUAGE);
            docKey[0] = new EntityCoordinator<>(client, QueryDoc.class).persist(doc);
        });
        if (docKey[0] != null) result.keys = Map.of("query_doc", docKey[0]);
        steps.add(result);
    }

    /**
     * Issues the filtered search and the count aggregate, both built with the client library's own
     * builder API ({@code Query.of}/{@code Query.aggregate}) and executed through
     * {@code EntityCoordinator}, never through a raw generated stub. Row keys and the metric value
     * are reported verbatim; the orchestrator decides what they mean.
     */
    private static void doQueryRead(IversonClient client, String idPrefix, List<StepResult> steps) {
        steps.add(step("search_by_marker", r -> {
            QueryBuilder<QueryDoc> query = Query.of(QueryDoc.class).where("marker").eq(idPrefix).limit(100);
            List<EntityCoordinator.SearchResult<QueryDoc>> hits =
                new EntityCoordinator<>(client, QueryDoc.class).search(query);
            List<String> keys = new ArrayList<>();
            for (EntityCoordinator.SearchResult<QueryDoc> hit : hits) {
                keys.add(hit.entity().getId() == null ? null : hit.entity().getId().toString());
            }
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("keys", keys);
            r.entity = GSON.toJsonTree(reported);
        }));

        steps.add(step("aggregate_count", r -> {
            AggregateBuilder aggregate = Query.aggregate("QueryDoc")
                .where("marker", SearchOperator.EQUALS, idPrefix)
                .countAll("count");
            ObjectSearch.AggregateResponse response =
                new EntityCoordinator<>(client, QueryDoc.class).aggregate(aggregate);
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put(
                "value",
                response.getResultsCount() > 0 ? response.getResults(0).getMetricValue() : null);
            reported.put("total", response.getTotal());
            r.entity = GSON.toJsonTree(reported);
        }));
    }

    // ── S9 error-contract ────────────────────────────────────────────────────────────────────

    /**
     * Seeds one {@code ErrorDoc} row so the read phase's positive control has something real to
     * find. The key is reported whenever {@code persist} returned one.
     */
    private static void doErrorContractWrite(
            IversonClient client, String tenant, String ownerId, String idPrefix, List<StepResult> steps) {
        String[] docKey = new String[1];
        StepResult result = step("write_error_doc", r -> {
            ErrorDoc doc = new ErrorDoc();
            doc.setTenantId(tenant);
            doc.setOwnerId(ownerId);
            doc.setLabel("error-" + LANGUAGE + "-" + idPrefix);
            docKey[0] = new EntityCoordinator<>(client, ErrorDoc.class).persist(doc);
        });
        if (docKey[0] != null) result.keys = Map.of("error_doc", docKey[0]);
        steps.add(result);
    }

    /**
     * The three observations S9 grades: a positive control through the same read method (the ERR
     * backstop), a read of a key no row exists under, and a mapped write against a type the server
     * holds no schema for. Every status code and found/not-found flag is DATA to report — the
     * orchestrator is the only thing that judges any of it.
     */
    private static void doErrorContractRead(
            IversonClient client,
            String tenant,
            String ownerId,
            String idPrefix,
            Map<String, String> priorKeys,
            List<StepResult> steps) {
        UUID rowKey = keyFor(priorKeys, idPrefix, "error_doc");

        // The positive control (the ERR backstop). Same client method, same type and same acting
        // user as the absent-key read below — only the key differs, which is what makes "reports
        // absence" evidence rather than a property of a read path that finds nothing ever.
        steps.add(step("read_present_row", r -> {
            ErrorDoc present = new EntityCoordinator<>(client, ErrorDoc.class).getMapped(rowKey.toString(), 0);
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("found", present != null);
            reported.put("key", present == null || present.getId() == null ? null : present.getId().toString());
            r.entity = GSON.toJsonTree(reported);
        }));

        // The absent-key read. The key is freshly generated and never written, so no row can exist
        // under it. The server answers with a SUCCESSFUL RPC carrying success=false, and this client
        // library renders that as null — reported as found:false with no status code. A library that
        // threw instead would land in the StatusRuntimeException branch and report a code, which is
        // exactly what IVC-ERR-004's second assertion grades.
        StepResult missing = new StepResult("read_missing_row");
        try {
            ErrorDoc absent = new EntityCoordinator<>(client, ErrorDoc.class)
                .getMapped(UUID.randomUUID().toString(), 0);
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("found", absent != null);
            reported.put("statusCode", null);
            missing.entity = GSON.toJsonTree(reported);
        } catch (StatusRuntimeException rpc) {
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("found", null);
            reported.put("statusCode", rpc.getStatus().getCode().value());
            reported.put("status", rpc.getStatus().getCode().name());
            reported.put("detail", rpc.getStatus().getDescription());
            missing.entity = GSON.toJsonTree(reported);
        } catch (Exception e) {
            // Not a gRPC status at all — the attempt never produced an observation.
            missing.ok = false;
            missing.error = describe(e);
        }
        steps.add(missing);

        // The unregistered-type write. RequireSchema runs before authorization and before relation
        // validation in ObjectMappingGrpcService.Post, so the refusal is attributable to the missing
        // schema and to nothing else. Status code AND detail are reported: the detail is what proves
        // this client library hands the server's message to the caller rather than substituting
        // wording of its own.
        StepResult unregistered = new StepResult("write_unregistered_type");
        try {
            ErrorUnregisteredDoc doc = new ErrorUnregisteredDoc();
            doc.setTenantId(tenant);
            doc.setOwnerId(ownerId);
            doc.setLabel("error-unregistered-" + LANGUAGE + "-" + idPrefix);
            new EntityCoordinator<>(client, ErrorUnregisteredDoc.class).postMapped(doc);

            // The server accepted a write against a type it has no schema for. Reported as a
            // missing status code rather than judged here.
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("statusCode", null);
            reported.put("status", "succeeded");
            unregistered.entity = GSON.toJsonTree(reported);
        } catch (StatusRuntimeException rpc) {
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("statusCode", rpc.getStatus().getCode().value());
            reported.put("status", rpc.getStatus().getCode().name());
            reported.put("detail", rpc.getStatus().getDescription());
            unregistered.entity = GSON.toJsonTree(reported);
        } catch (Exception e) {
            unregistered.ok = false;
            unregistered.error = describe(e);
        }
        steps.add(unregistered);
    }

    // ── S8 identity ──────────────────────────────────────────────────────────────────────────

    /**
     * Creates one {@code IdentityDoc} row under this driver's OWN acting user, carrying a
     * deliberately wrong tenant value ({@link #IDENTITY_WRONG_TENANT}). The key is reported
     * whenever {@code persist} returned one: the orchestrator's backstop is exactly "this language
     * reported a key", and the negative leg is only a denial while the row exists.
     */
    private static void doIdentityWrite(
            IversonClient client, String ownerId, String idPrefix, List<StepResult> steps) {
        String[] docKey = new String[1];
        StepResult result = step("write_identity_doc", r -> {
            IdentityDoc doc = new IdentityDoc();
            doc.setTenantId(IDENTITY_WRONG_TENANT);
            doc.setOwnerId(ownerId);
            doc.setLabel("identity-" + LANGUAGE + "-" + idPrefix);
            docKey[0] = new EntityCoordinator<>(client, IdentityDoc.class).persist(doc);
        });
        if (docKey[0] != null) result.keys = Map.of("identity_doc", docKey[0]);
        steps.add(result);
    }

    /**
     * The positive leg (read the row back under this driver's own acting user) and the negative leg
     * (attempt one update under {@code --wrong-acting-token}, an acting user of another tenant, and
     * report the gRPC status code that attempt received).
     *
     * <p>The wrong identity is a second {@link IversonClient} over the SAME channel carrying a
     * second {@link DualHeaderCredentials}: the acting-user header is a per-client credential here,
     * so no second channel is needed. The service identity is unchanged, so the only thing that
     * differs between this call and an allowed one is which end user it acts as. The status code is
     * DATA to report, never an error to judge — that is the orchestrator's job.
     */
    private static void doIdentityRead(
            IversonClient client,
            ManagedChannel channel,
            Args parsedArgs,
            String tenant,
            String ownerId,
            String idPrefix,
            Map<String, String> priorKeys,
            List<StepResult> steps) {
        UUID rowKey = keyFor(priorKeys, idPrefix, "identity_doc");

        // Reported as the deliberately minimal, cross-language-identical projection all five
        // drivers emit — a driver-native serialization would differ per language (this one omits
        // nulls) and make a naming difference render as a conformance failure.
        steps.add(step("read_identity_doc", r -> {
            IdentityDoc readBack =
                new EntityCoordinator<>(client, IdentityDoc.class).getMapped(rowKey.toString(), 0);
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("key", readBack == null || readBack.getId() == null ? null : readBack.getId().toString());
            reported.put("tenant", readBack == null ? null : readBack.getTenantId());
            reported.put("owner", readBack == null ? null : readBack.getOwnerId());
            r.entity = GSON.toJsonTree(reported);
        }));

        StepResult denied = new StepResult("denied_update_wrong_acting_user");
        try {
            DualHeaderCredentials wrongCredentials = new DualHeaderCredentials(
                parsedArgs.optional("--client-id"),
                parsedArgs.optional("--client-secret"),
                parsedArgs.optional("--token-endpoint"),
                parsedArgs.optional("--wrong-acting-token"),
                parsedArgs.optional("--service-token"));
            IversonClient wrongClient = new IversonClient(channel, wrongCredentials);

            // The update payload's tenant value no longer affects the outcome on THIS leg. It USED to: the
            // server once rejected an existing row's payload tenant that differed from the caller's claim as
            // "Tenant field is immutable" — also PermissionDenied (7), fired for ANY caller including the
            // right one — so a wrong tenant here would have made this step green while proving nothing about
            // which end user is calling. That branch compares AuthorizationDecision.TenantColumn, which for
            // any type registered by a current server build is the SERVER-OWNED __TenantId column — and a
            // payload may never carry that name (it is rejected with InvalidArgument several branches
            // earlier), so against a freshly registered type the branch cannot fire. It is NOT dead code:
            // SchemaRegistry.LoadAsync rehydrates pre-cutover _iverson_schema rows verbatim, so on an upgraded
            // deployment TenantColumn can still be a client-declared name such as "TenantId" — which the
            // InvalidArgument guard does not match — and the immutability branch fires there today. The
            // conformance harness registers its types fresh, which is the ONLY reason this leg is insensitive
            // to the payload tenant. The refusal this step observes is the tenant MISMATCH between the
            // existing row's __TenantId and this wrong acting user's own claim. The acting user's real tenant
            // is still sent here so this leg keeps sending a payload a conforming client would send.
            IdentityDoc doc = new IdentityDoc();
            doc.setId(rowKey);
            doc.setTenantId(tenant);
            doc.setOwnerId(ownerId);
            doc.setLabel("identity-" + LANGUAGE + "-" + idPrefix + "-updated-by-the-wrong-user");
            new EntityCoordinator<>(wrongClient, IdentityDoc.class).updateMapped(doc);

            // The server accepted the wrong acting user's write. Reported as a missing status code
            // rather than judged here.
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("statusCode", null);
            reported.put("status", "succeeded");
            denied.entity = GSON.toJsonTree(reported);
        } catch (StatusRuntimeException rpc) {
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("statusCode", rpc.getStatus().getCode().value());
            reported.put("status", rpc.getStatus().getCode().name());
            reported.put("detail", rpc.getStatus().getDescription());
            denied.entity = GSON.toJsonTree(reported);
        } catch (Exception e) {
            // Not a gRPC status at all — the attempt never produced an observation.
            denied.ok = false;
            denied.error = describe(e);
        }
        steps.add(denied);
    }

    // ── S7 vector-search ─────────────────────────────────────────────────────────────────────

    /**
     * Seeds one {@code VectorDoc} row stamped with the run's marker and this language's label. The
     * key is reported whenever {@code persist} returned one — it is the orchestrator's expected-set
     * accounting for BOTH vector requirements.
     */
    private static void doVectorSearchWrite(
            IversonClient client, String tenant, String ownerId, String idPrefix, List<StepResult> steps) {
        String[] docKey = new String[1];
        StepResult result = step("write_vector_doc", r -> {
            VectorDoc doc = new VectorDoc();
            doc.setTenantId(tenant);
            doc.setOwnerId(ownerId);
            doc.setMarker(idPrefix);
            doc.setTitle("vector search conformance note from " + LANGUAGE);
            doc.setBody("This passage exists so the " + LANGUAGE + " conformance driver has a chunked "
                + "body to retrieve. It is short on purpose: one window per row keeps the "
                + "orchestrator's parent-key comparison exact.");
            doc.setLabel(VECTOR_DOC_LABEL);
            docKey[0] = new EntityCoordinator<>(client, VectorDoc.class).persist(doc);
        });
        if (docKey[0] != null) result.keys = Map.of("vector_doc", docKey[0]);
        steps.add(result);
    }

    /**
     * Issues the similarity search and the chunk search, both built with the client library's own
     * vector-search builders ({@code Query.similar}/{@code Query.chunks}) and executed through
     * {@code EntityCoordinator}, never through a raw generated stub. Row labels and chunk parent
     * keys are reported verbatim; the orchestrator decides what they mean.
     */
    private static void doVectorSearchRead(IversonClient client, String idPrefix, List<StepResult> steps) {
        steps.add(step("search_similar_by_title", r -> {
            SimilarBuilder query = Query.similar("VectorDoc", "Title")
                .text(VECTOR_QUERY_TEXT)
                .topK(VECTOR_TOP_K)
                .where("Marker", SearchOperator.EQUALS, idPrefix);
            List<EntityCoordinator.SearchResult<VectorDoc>> hits =
                new EntityCoordinator<>(client, VectorDoc.class).searchSimilar(query);
            List<String> labels = new ArrayList<>();
            for (EntityCoordinator.SearchResult<VectorDoc> hit : hits) {
                labels.add(hit.entity().getLabel());
            }
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("labels", labels);
            r.entity = GSON.toJsonTree(reported);
        }));

        steps.add(step("search_chunks_by_marker", r -> {
            ChunksBuilder query = Query.chunks("VectorDoc", "Body")
                .text(VECTOR_QUERY_TEXT)
                .topK(VECTOR_TOP_K)
                .where("Marker", SearchOperator.EQUALS, idPrefix);
            List<EntityCoordinator.ChunkSearchResult> found =
                new EntityCoordinator<>(client, VectorDoc.class).searchChunks(query);
            List<String> parentKeys = new ArrayList<>();
            for (EntityCoordinator.ChunkSearchResult chunk : found) {
                parentKeys.add(chunk.parentKey());
            }
            Map<String, Object> reported = new java.util.LinkedHashMap<>();
            reported.put("parentKeys", parentKeys);
            r.entity = GSON.toJsonTree(reported);
        }));
    }

    // ── write ────────────────────────────────────────────────────────────────────────────────

    /**
     * One step per row: a denied or failed write must not abort the other two. Keys are now
     * server-assigned — create requests must omit Id entirely — so each row's key is only known,
     * and only reported, when the write actually returns one. {@code EntityCoordinator.persist}
     * returns the server-assigned key (the lightweight {@code ObjectPersistenceService}, unlike
     * the heavier mapping RPC), so {@code entity} stays null here — that is genuinely what the
     * client returned.
     */
    private static void doWrite(
            IversonClient client, String tenant, String ownerId, String idPrefix, List<StepResult> steps) {
        String[] authorKey = new String[1];
        String[] tagKey = new String[1];

        StepResult authorStep = step("write_author", r -> {
            JavaAuthor author = new JavaAuthor();
            author.setTenantId(tenant);
            author.setOwnerId(ownerId);
            author.setName("author-" + idPrefix);
            authorKey[0] = new EntityCoordinator<>(client, JavaAuthor.class).persist(author);
        });
        if (authorKey[0] != null) authorStep.keys = Map.of("author", authorKey[0]);
        steps.add(authorStep);

        StepResult tagStep = step("write_tag", r -> {
            JavaTag tag = new JavaTag();
            tag.setTenantId(tenant);
            tag.setOwnerId(ownerId);
            tag.setLabel("tag-" + idPrefix);
            tagKey[0] = new EntityCoordinator<>(client, JavaTag.class).persist(tag);
        });
        if (tagKey[0] != null) tagStep.keys = Map.of("tag", tagKey[0]);
        steps.add(tagStep);

        String[] articleKey = new String[1];

        StepResult articleStep = step("write_article", r -> {
            JavaArticle article = new JavaArticle();
            article.setTenantId(tenant);
            article.setOwnerId(ownerId);
            article.setTitle("title-" + idPrefix);
            if (authorKey[0] != null) article.setJavaAuthorId(UUID.fromString(authorKey[0]));
            if (tagKey[0] != null) article.setJavaTagIds(List.of(UUID.fromString(tagKey[0])));
            if (tagKey[0] != null) article.setJavaTagId(UUID.fromString(tagKey[0]));
            articleKey[0] = new EntityCoordinator<>(client, JavaArticle.class).persist(article);
        });
        if (articleKey[0] != null) articleStep.keys = Map.of("article", articleKey[0]);
        steps.add(articleStep);
    }

    // ── read ─────────────────────────────────────────────────────────────────────────────────

    /** Two gets at depth 0 (the proto's default), reported separately. */
    private static void doRead(
            IversonClient client, String idPrefix, Map<String, String> priorKeys, List<StepResult> steps) {
        UUID articleKey = keyFor(priorKeys, idPrefix, "article");
        UUID authorKey = keyFor(priorKeys, idPrefix, "author");

        steps.add(step("get", r -> {
            JavaArticle article = new EntityCoordinator<>(client, JavaArticle.class).get(articleKey.toString());
            r.entity = article == null ? null : GSON.toJsonTree(article);
        }));

        steps.add(step("get_author", r -> {
            JavaAuthor author = new EntityCoordinator<>(client, JavaAuthor.class).get(authorKey.toString());
            r.entity = author == null ? null : GSON.toJsonTree(author);
        }));

        // IVC-LIFE-006/IVC-LIFE-007: a depth-1 read through this driver's OWN client library,
        // reported as its own step — proves the CLIENT can express the request (LIFE-006) and
        // materialize the hydrated result (LIFE-007), distinct from the orchestrator's own
        // depth-1 MappingGet which only proves the SERVER hydrates.
        steps.add(step("get_depth1", r -> {
            JavaArticle article = new EntityCoordinator<>(client, JavaArticle.class).getMapped(articleKey.toString(), 1);
            r.entity = article == null ? null : GSON.toJsonTree(article);
        }));
    }

    // ── update ───────────────────────────────────────────────────────────────────────────────

    /** {@code EntityCoordinator.update} returns void, so {@code entity} stays null here too. */
    private static void doUpdate(
            IversonClient client, String tenant, String ownerId, String idPrefix,
            Map<String, String> priorKeys, List<StepResult> steps) {
        UUID articleKey = keyFor(priorKeys, idPrefix, "article");
        UUID authorKey = keyFor(priorKeys, idPrefix, "author");
        UUID tagKey = keyFor(priorKeys, idPrefix, "tag");

        steps.add(step("update", r -> {
            JavaArticle article = new JavaArticle();
            article.setId(articleKey);
            article.setTenantId(tenant);
            article.setOwnerId(ownerId);
            article.setTitle("title-" + idPrefix + "-updated");
            article.setJavaAuthorId(authorKey);
            article.setJavaTagIds(List.of(tagKey));
            article.setJavaTagId(tagKey);
            new EntityCoordinator<>(client, JavaArticle.class).update(article);
        }));
    }

    // ── delete ───────────────────────────────────────────────────────────────────────────────

    /**
     * The read-back is its own step, carrying {@code entity} (null when nothing came back) and
     * the client's own error text in {@code error} — never rely on absence alone.
     */
    private static void doDelete(
            IversonClient client, String idPrefix, Map<String, String> priorKeys, List<StepResult> steps) {
        UUID articleKey = keyFor(priorKeys, idPrefix, "article");
        EntityCoordinator<JavaArticle> coordinator = new EntityCoordinator<>(client, JavaArticle.class);

        steps.add(step("delete", r -> coordinator.delete(articleKey.toString())));

        steps.add(step("get_after_delete", r -> {
            JavaArticle article = coordinator.get(articleKey.toString());
            r.entity = article == null ? null : GSON.toJsonTree(article);
        }));
    }

    // ── S4 interop ───────────────────────────────────────────────────────────────────────────

    /** Writes SharedAuthor then SharedArticle, reporting keys "shared_author" and "shared_article". */
    private static void doInteropWrite(
            IversonClient client, String tenant, String ownerId, String idPrefix, List<StepResult> steps) {
        String[] authorKey = new String[1];

        StepResult authorStep = step("write_shared_author", r -> {
            SharedAuthor author = new SharedAuthor();
            author.setTenantId(tenant);
            author.setOwnerId(ownerId);
            author.setName("shared-author-" + idPrefix);
            authorKey[0] = new EntityCoordinator<>(client, SharedAuthor.class).persist(author);
        });
        if (authorKey[0] != null) authorStep.keys = Map.of("shared_author", authorKey[0]);
        steps.add(authorStep);

        String[] articleKey = new String[1];

        StepResult articleStep = step("write_shared_article", r -> {
            SharedArticle article = new SharedArticle();
            article.setTenantId(tenant);
            article.setOwnerId(ownerId);
            article.setTitle("shared-title-" + idPrefix);
            if (authorKey[0] != null) article.setSharedAuthorId(UUID.fromString(authorKey[0]));
            articleKey[0] = new EntityCoordinator<>(client, SharedArticle.class).persist(article);
        });
        if (articleKey[0] != null) articleStep.keys = Map.of("shared_article", articleKey[0]);
        steps.add(articleStep);
    }

    /**
     * Iterates every language's reported "shared_article" key from the full --keys map (not just
     * this driver's own slice), so this one driver invocation reads all five languages' rows —
     * the fan-out that produces 25 reads across the five drivers.
     */
    private static void doInteropRead(IversonClient client, String keysJson, List<StepResult> steps) {
        Map<String, Map<String, String>> allKeys = Keys.parseAll(keysJson);
        EntityCoordinator<SharedArticle> coordinator = new EntityCoordinator<>(client, SharedArticle.class);

        allKeys.keySet().stream().sorted().forEach(writerLanguage -> {
            String key = allKeys.get(writerLanguage).get("shared_article");
            if (key == null || key.isEmpty()) return;

            steps.add(step("read_shared_article_" + writerLanguage, r -> {
                SharedArticle article = coordinator.get(key);
                r.entity = article == null ? null : GSON.toJsonTree(article);
            }));
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    @FunctionalInterface
    private interface StepBody {
        void run(StepResult result) throws Exception;
    }

    /** A throwing step is data, not a driver failure: it becomes ok:false with an error text. */
    private static StepResult step(String name, StepBody body) {
        StepResult result = new StepResult(name);
        try {
            body.run(result);
        } catch (Exception e) {
            result.ok = false;
            result.error = describe(e);
        }
        return result;
    }

    private static String describe(Exception e) {
        if (e instanceof StatusRuntimeException rpc) {
            return rpc.getStatus().getCode() + ": " + rpc.getStatus().getDescription();
        }
        return e.getClass().getSimpleName() + ": " + e.getMessage();
    }

    private static UUID keyFor(Map<String, String> priorKeys, String idPrefix, String logicalName) {
        String existing = priorKeys.get(logicalName);
        if (existing != null) {
            try {
                return UUID.fromString(existing);
            } catch (IllegalArgumentException ignored) {
                // Falls through to re-derivation below.
            }
        }
        return Keys.derive(idPrefix, logicalName);
    }

    /** One step's outcome, serialized to the phase document the orchestrator reads. */
    private static final class StepResult {
        final String name;
        boolean ok = true;
        String error;
        JsonElement typeDescriptor;
        Map<String, String> keys;
        JsonElement entity;

        StepResult(String name) {
            this.name = name;
        }
    }

    /** The whole --out document for one phase invocation. camelCase, matching DriverProtocol.cs. */
    private static final class PhaseDocument {
        final String language;
        final String phase;
        final List<StepResult> steps;

        PhaseDocument(String language, String phase, List<StepResult> steps) {
            this.language = language;
            this.phase = phase;
            this.steps = steps;
        }
    }

    /**
     * Keys are driver-chosen UUIDs derived from the run id, so two runs never collide and every
     * phase after `write` can re-derive nothing — it reads them back from --keys.
     */
    private static final class Keys {

        static UUID derive(String idPrefix, String logicalName) {
            try {
                MessageDigest md5 = MessageDigest.getInstance("MD5");
                byte[] digest = md5.digest((idPrefix + ":" + logicalName).getBytes(StandardCharsets.UTF_8));
                long mostSigBits = 0;
                long leastSigBits = 0;
                for (int i = 0; i < 8; i++) mostSigBits = (mostSigBits << 8) | (digest[i] & 0xffL);
                for (int i = 8; i < 16; i++) leastSigBits = (leastSigBits << 8) | (digest[i] & 0xffL);
                return new UUID(mostSigBits, leastSigBits);
            } catch (NoSuchAlgorithmException e) {
                throw new IllegalStateException("MD5 not available", e);
            }
        }

        /** Reads this language's slice out of the language-qualified --keys map. */
        static Map<String, String> parse(String keysJson, String language) {
            if (keysJson == null || keysJson.isBlank()) return Map.of();

            Type type = new TypeToken<Map<String, Map<String, String>>>() {}.getType();
            Map<String, Map<String, String>> byLanguage = GSON.fromJson(keysJson, type);
            if (byLanguage == null) return Map.of();

            Map<String, String> mine = byLanguage.get(language);
            return mine == null ? Map.of() : mine;
        }

        /** The full language-qualified --keys map, unlike {@link #parse} which slices out one
         * language. S4 interop's read phase needs every language's reported "shared_article" key,
         * not just this driver's own. */
        static Map<String, Map<String, String>> parseAll(String keysJson) {
            if (keysJson == null || keysJson.isBlank()) return Map.of();

            Type type = new TypeToken<Map<String, Map<String, String>>>() {}.getType();
            Map<String, Map<String, String>> byLanguage = GSON.fromJson(keysJson, type);
            return byLanguage == null ? Map.of() : byLanguage;
        }
    }

    private static final class Args {
        private final Map<String, String> values = new java.util.HashMap<>();

        static Args parse(String[] argv) {
            Args parsed = new Args();
            for (int i = 0; i < argv.length; i++) {
                String flag = argv[i];
                if (!flag.startsWith("--")) continue;
                // The next argument is the value whatever it looks like: the harness always emits
                // `--flag <value>` pairs (empty string included), and legitimate values — a
                // base64 token, a JSON blob — can begin with "--". Treating a leading "--" as
                // "no value" would silently drop them.
                String value = "";
                if (i + 1 < argv.length) {
                    value = argv[++i];
                }
                parsed.values.put(flag, value);
            }
            return parsed;
        }

        String require(String flag) {
            String value = values.get(flag);
            if (value == null || value.isEmpty()) {
                throw new IllegalArgumentException("missing required flag " + flag);
            }
            return value;
        }

        String optional(String flag) {
            String value = values.get(flag);
            return value == null || value.isEmpty() ? null : value;
        }
    }
}
