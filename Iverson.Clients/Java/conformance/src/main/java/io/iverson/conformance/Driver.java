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
import io.iverson.conformance.models.SharedArticle;
import io.iverson.conformance.models.SharedAuthor;
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
    private static final java.util.Set<String> SUPPORTED_SCENARIOS =
        java.util.Set.of(CRUD_ROUNDTRIP_SCENARIO, INTEROP_SCENARIO, SCHEMA_CATALOG_SCENARIO);
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
