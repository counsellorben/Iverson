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
    private static final String SCENARIO = "crud-roundtrip";
    private static final Gson GSON = new Gson();

    private Driver() {}

    public static void main(String[] args) throws Exception {
        Args parsedArgs = Args.parse(args);

        String scenario = parsedArgs.require("--scenario");
        if (!SCENARIO.equals(scenario)) {
            System.err.println(
                "unsupported scenario '" + scenario + "'; this driver implements only '" + SCENARIO + "'");
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
            parsedArgs.optional("--acting-token"));

        ManagedChannel channel = ManagedChannelBuilder.forAddress(grpcUri.getHost(), grpcUri.getPort())
            .usePlaintext()
            .intercept(capture)
            .build();

        List<StepResult> steps = new ArrayList<>();
        try {
            IversonClient client = new IversonClient(channel, credentials);

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

    // ── write ────────────────────────────────────────────────────────────────────────────────

    /**
     * One step per row: a denied or failed write must not abort the other two, and each row's
     * key is reported unconditionally so later phases can address the row even when this write
     * failed. {@code EntityCoordinator.persist} returns only the server-assigned key (the
     * lightweight {@code ObjectPersistenceService}, unlike the heavier mapping RPC), so
     * {@code entity} stays null here — that is genuinely what the client returned.
     */
    private static void doWrite(
            IversonClient client, String tenant, String ownerId, String idPrefix, List<StepResult> steps) {
        UUID authorKey = Keys.derive(idPrefix, "author");
        UUID tagKey = Keys.derive(idPrefix, "tag");
        UUID articleKey = Keys.derive(idPrefix, "article");

        StepResult authorStep = step("write_author", r -> {
            JavaAuthor author = new JavaAuthor();
            author.setId(authorKey);
            author.setTenantId(tenant);
            author.setOwnerId(ownerId);
            author.setName("author-" + idPrefix);
            new EntityCoordinator<>(client, JavaAuthor.class).persist(author);
        });
        authorStep.keys = Map.of("author", authorKey.toString());
        steps.add(authorStep);

        StepResult tagStep = step("write_tag", r -> {
            JavaTag tag = new JavaTag();
            tag.setId(tagKey);
            tag.setTenantId(tenant);
            tag.setOwnerId(ownerId);
            tag.setLabel("tag-" + idPrefix);
            new EntityCoordinator<>(client, JavaTag.class).persist(tag);
        });
        tagStep.keys = Map.of("tag", tagKey.toString());
        steps.add(tagStep);

        StepResult articleStep = step("write_article", r -> {
            JavaArticle article = new JavaArticle();
            article.setId(articleKey);
            article.setTenantId(tenant);
            article.setOwnerId(ownerId);
            article.setTitle("title-" + idPrefix);
            article.setJavaAuthorId(authorKey);
            article.setJavaTagIds(List.of(tagKey));
            new EntityCoordinator<>(client, JavaArticle.class).persist(article);
        });
        articleStep.keys = Map.of("article", articleKey.toString());
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
