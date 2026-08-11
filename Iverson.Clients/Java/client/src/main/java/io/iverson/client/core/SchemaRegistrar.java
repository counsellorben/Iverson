package io.iverson.client.core;

import io.grpc.StatusRuntimeException;
import io.iverson.client.annotations.*;
import iverson.ObjectMapping;
import iverson.ObjectMapping.ClrType;
import iverson.ObjectMapping.PropertyDescriptor;
import iverson.ObjectMapping.RelationDescriptor;
import iverson.ObjectMapping.RelationKind;
import iverson.ObjectMapping.SchemaRequest;
import iverson.ObjectMapping.SchemaResponse;
import iverson.ObjectMapping.TypeDescriptor;
import iverson.ObjectMappingServiceGrpc;

import java.lang.reflect.Field;
import java.time.LocalDateTime;
import java.time.OffsetDateTime;
import java.util.*;

/**
 * Reflects over annotated entity classes and registers their schemas with the
 * Iverson server via the {@code ObjectMappingService.RegisterSchema} RPC.
 *
 * <p>Typical usage at application startup:</p>
 * <pre>{@code
 * SchemaRegistrar registrar = new SchemaRegistrar(client);
 * registrar.registerAll(Author.class, Tag.class, Article.class);
 * }</pre>
 */
public final class SchemaRegistrar {

    private final ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub stub;

    public SchemaRegistrar(IversonClient client) {
        this.stub = client.mappingStub;
    }

    /** Package-private constructor for testing with a mock stub. */
    SchemaRegistrar(ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub stub) {
        this.stub = stub;
    }

    /**
     * Reflects on each class, builds a {@link TypeDescriptor}, and calls
     * {@code RegisterSchema} for each one.
     *
     * @throws StatusRuntimeException if the server rejects any registration
     */
    public void registerAll(Class<?>... classes) {
        for (Class<?> cls : classes) {
            if (cls.getAnnotation(IversonEntity.class) == null) {
                throw new IllegalArgumentException(
                    cls.getSimpleName() + " is not annotated with @IversonEntity");
            }
            TypeDescriptor descriptor = buildTypeDescriptor(cls);
            SchemaRequest request = SchemaRequest.newBuilder()
                .setRootType(descriptor)
                .build();
            SchemaResponse response = stub.registerSchema(request);
            if (!response.getSuccess()) {
                throw new StatusRuntimeException(
                    io.grpc.Status.INTERNAL.withDescription(
                        "Schema registration failed for " + cls.getSimpleName() +
                        ": " + response.getError()));
            }
        }
    }

    // ── TypeDescriptor construction ────────────────────────────────────────────

    TypeDescriptor buildTypeDescriptor(Class<?> cls) {
        TypeDescriptor.Builder builder = TypeDescriptor.newBuilder()
            .setTypeName(cls.getSimpleName());

        IversonDescription typeDescription = cls.getAnnotation(IversonDescription.class);
        if (typeDescription != null) {
            builder.setDescription(typeDescription.value());
        }

        // Collect nav property field names (annotated with any relation annotation)
        List<Field> allFields = getAllFields(cls);
        Set<String> navFieldNames = new HashSet<>();
        Field keyField = null;
        List<Field> tenantFields = new ArrayList<>();

        for (Field field : allFields) {
            if (field.getAnnotation(IversonKey.class) != null) {
                keyField = field;
            }
            if (field.getAnnotation(IversonTenant.class) != null) {
                tenantFields.add(field);
            }
            if (isRelationField(field)) {
                navFieldNames.add(field.getName());
            }
        }

        if (keyField == null) {
            throw new IllegalArgumentException(
                cls.getSimpleName() + " has no field annotated with @IversonKey");
        }

        validateKeyDeclarations(cls, keyField);

        // Key property first
        builder.addProperties(buildKeyDescriptor(keyField));

        // Non-key scalar properties
        for (Field field : allFields) {
            if (field.getName().equals(keyField.getName())) continue;
            if (navFieldNames.contains(field.getName())) continue;
            PropertyDescriptor pd = tryBuildPropertyDescriptor(field);
            if (pd != null) builder.addProperties(pd);
        }

        builder.setTenantField(resolveTenantField(cls, tenantFields));

        // Relation descriptors
        for (Field field : allFields) {
            if (!isRelationField(field)) continue;
            RelationDescriptor rd = buildRelationDescriptor(field, cls.getSimpleName());
            if (rd != null) builder.addRelations(rd);
        }

        return builder.build();
    }

    /**
     * Rejects declarations the server silently discards on a key field. The server builds every
     * per-property declaration from non-key properties only, so anything but a description on
     * the key is accepted and dropped without error.
     */
    private static void validateKeyDeclarations(Class<?> cls, Field keyField) {
        List<String> rejected = new ArrayList<>();

        if (keyField.getAnnotation(IversonSearchKey.class) != null)  rejected.add("@IversonSearchKey");
        if (keyField.getAnnotation(IversonLargeField.class) != null) rejected.add("@IversonLargeField");
        if (keyField.getAnnotation(IversonEmbedding.class) != null)  rejected.add("@IversonEmbedding");
        if (keyField.getAnnotation(IversonChunk.class) != null)      rejected.add("@IversonChunk");
        if (keyField.getAnnotation(IversonMetadata.class) != null)   rejected.add("@IversonMetadata");
        if (keyField.getAnnotation(IversonSummary.class) != null)    rejected.add("@IversonSummary");
        if (keyField.getAnnotation(IversonKeywords.class) != null)   rejected.add("@IversonKeywords");
        if (keyField.getAnnotation(IversonExtracted.class) != null)  rejected.add("@IversonExtracted");

        if (rejected.isEmpty()) return;

        throw new IllegalArgumentException(
            cls.getSimpleName() + "." + keyField.getName() + " is the primary key and also declares " +
            String.join(", ", rejected) + "; the server builds every per-property declaration " +
            "from non-key properties only, so this would be accepted and silently discarded. " +
            "Remove it from the key field. (Only a description is valid on a key.)");
    }

    private static String resolveTenantField(Class<?> cls, List<Field> tenantFields) {
        if (tenantFields.isEmpty()) {
            throw new IllegalArgumentException(
                cls.getSimpleName() + " has no field annotated with @IversonTenant; the server " +
                "requires every schema to declare a tenant boundary and will reject registration " +
                "without one.");
        }

        if (tenantFields.size() > 1) {
            StringBuilder names = new StringBuilder();
            for (Field field : tenantFields) {
                if (names.length() > 0) names.append(", ");
                names.append(field.getName());
            }
            throw new IllegalArgumentException(
                cls.getSimpleName() + " has multiple fields annotated with @IversonTenant (" +
                names + "); exactly one field must carry the tenant marker.");
        }

        return StructConverter.toPascalCase(tenantFields.get(0).getName());
    }

    private PropertyDescriptor buildKeyDescriptor(Field field) {
        DetectedType detected = detectClrType(field.getGenericType());
        ClrType clrType = detected != null ? detected.clrType() : ClrType.CLR_STRING; // fallback
        boolean isArray = detected != null && detected.isArray();
        PropertyDescriptor.Builder b = PropertyDescriptor.newBuilder()
            .setName(StructConverter.toPascalCase(field.getName()))
            .setClrType(clrType)
            .setIsKey(true)
            .setIsNullable(false)
            .setIsArray(isArray);
        applyAnnotations(b, field);
        return b.build();
    }

    private PropertyDescriptor tryBuildPropertyDescriptor(Field field) {
        DetectedType detected = detectClrType(field.getGenericType());
        if (detected == null) return null;

        boolean isNullable = !field.getType().isPrimitive();
        PropertyDescriptor.Builder b = PropertyDescriptor.newBuilder()
            .setName(StructConverter.toPascalCase(field.getName()))
            .setClrType(detected.clrType())
            .setIsKey(false)
            .setIsNullable(isNullable)
            .setIsArray(detected.isArray());
        applyAnnotations(b, field);
        return b.build();
    }

    private void applyAnnotations(PropertyDescriptor.Builder b, Field field) {
        IversonSearchKey sk = field.getAnnotation(IversonSearchKey.class);
        if (sk != null) {
            b.setIsSearchKey(true);
            b.setSearchKeyOrder(sk.order());
        }

        if (field.getAnnotation(IversonMetadata.class) != null) {
            b.setIsMetadata(true);
        }

        IversonDescription desc = field.getAnnotation(IversonDescription.class);
        if (desc != null) {
            b.setDescription(desc.value());
        }

        IversonLargeField lf = field.getAnnotation(IversonLargeField.class);
        if (lf != null) {
            b.setIsLargeField(true);
        }

        IversonEmbedding emb = field.getAnnotation(IversonEmbedding.class);
        if (emb != null) {
            b.setIsEmbedding(true);
            b.setVectorDim(0);
            b.setModelId("");
        }

        IversonChunk chunk = field.getAnnotation(IversonChunk.class);
        if (chunk != null) {
            b.setIsChunk(true);
            b.setChunkMaxTokens(chunk.maxTokens());
            b.setChunkOverlap(chunk.overlap());
            b.setChunkModelId("");
            b.setChunkVectorDim(0);
            b.setChunkContextual(chunk.contextual());
        }

        if (field.getAnnotation(IversonSummary.class) != null) {
            b.setIsSummaryTarget(true);
        }

        if (field.getAnnotation(IversonKeywords.class) != null) {
            b.setIsKeywordsTarget(true);
        }

        IversonExtracted extracted = field.getAnnotation(IversonExtracted.class);
        if (extracted != null) {
            String hint = extracted.value();
            if (hint == null || hint.isBlank()) {
                throw new IllegalArgumentException(
                    "@IversonExtracted on field '" + field.getName() +
                    "' requires a non-blank extraction hint; the server treats an empty hint " +
                    "as \"not an extraction target\" and would silently drop it.");
            }
            b.setExtractHint(hint);
        }
    }

    private RelationDescriptor buildRelationDescriptor(Field field, String ownerTypeName) {
        RelationKind kind;
        Class<?> relatedType;

        ManyToOne mto = field.getAnnotation(ManyToOne.class);
        ManyToMany mtm = field.getAnnotation(ManyToMany.class);
        OneToMany otm = field.getAnnotation(OneToMany.class);
        OneToOne oto = field.getAnnotation(OneToOne.class);

        if (mto != null)      { kind = RelationKind.MANY_TO_ONE;  relatedType = mto.type(); }
        else if (mtm != null) { kind = RelationKind.MANY_TO_MANY; relatedType = mtm.type(); }
        else if (otm != null) { kind = RelationKind.ONE_TO_MANY;  relatedType = otm.type(); }
        else if (oto != null) { kind = RelationKind.ONE_TO_ONE;   relatedType = oto.type(); }
        else return null;

        String fk = inferForeignKey(kind, relatedType.getSimpleName(), ownerTypeName);

        return RelationDescriptor.newBuilder()
            .setPropertyName(StructConverter.toPascalCase(field.getName()))
            .setKind(kind)
            .setRelatedType(relatedType.getSimpleName())
            .setForeignKey(fk)
            .build();
    }

    // ── Type detection ─────────────────────────────────────────────────────────

    private record DetectedType(ClrType clrType, boolean isArray) {}

    private static DetectedType detectClrType(java.lang.reflect.Type type) {
        // byte[] is a primitive scalar — check before the array unwrap.
        if (type == byte[].class) return new DetectedType(ClrType.CLR_BYTES, false);

        if (type instanceof Class<?> c && c.isArray()) {
            ClrType element = detectClrType(c.getComponentType());
            return element == null ? null : new DetectedType(element, true);
        }
        if (type instanceof java.lang.reflect.ParameterizedType p
                && p.getRawType() instanceof Class<?> raw
                && java.util.Collection.class.isAssignableFrom(raw)) {
            java.lang.reflect.Type[] args = p.getActualTypeArguments();
            if (args.length == 1 && args[0] instanceof Class<?> elementClass) {
                ClrType element = detectClrType(elementClass);
                return element == null ? null : new DetectedType(element, true);
            }
            return null;
        }
        if (type instanceof Class<?> c) {
            ClrType scalar = detectClrType(c);
            return scalar == null ? null : new DetectedType(scalar, false);
        }
        return null;
    }

    private static ClrType detectClrType(Class<?> type) {
        if (type == String.class)              return ClrType.CLR_STRING;
        if (type == java.util.UUID.class)      return ClrType.CLR_GUID;
        if (type == int.class || type == Integer.class)   return ClrType.CLR_INT32;
        if (type == long.class || type == Long.class)     return ClrType.CLR_INT64;
        if (type == float.class || type == Float.class)   return ClrType.CLR_FLOAT;
        if (type == double.class || type == Double.class) return ClrType.CLR_DOUBLE;
        if (type == boolean.class || type == Boolean.class) return ClrType.CLR_BOOL;
        if (type == OffsetDateTime.class || type == LocalDateTime.class ||
            type == java.time.Instant.class)  return ClrType.CLR_DATETIME;
        if (type == byte[].class)              return ClrType.CLR_BYTES;
        // Unsupported (collection nav props, custom types, etc.)
        return null;
    }

    private static String inferForeignKey(RelationKind kind, String relatedTypeName, String thisTypeName) {
        return switch (kind) {
            case MANY_TO_ONE  -> relatedTypeName + "Id";
            case ONE_TO_ONE   -> relatedTypeName + "Id";
            case MANY_TO_MANY -> relatedTypeName + "Ids";
            case ONE_TO_MANY  -> thisTypeName + "Id";
            default           -> "";
        };
    }

    private static boolean isRelationField(Field field) {
        return field.getAnnotation(ManyToOne.class) != null
            || field.getAnnotation(ManyToMany.class) != null
            || field.getAnnotation(OneToMany.class) != null
            || field.getAnnotation(OneToOne.class) != null;
    }

    private static List<Field> getAllFields(Class<?> cls) {
        List<Field> fields = new ArrayList<>();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                if (!f.isSynthetic()) fields.add(f);
            }
            cls = cls.getSuperclass();
        }
        return fields;
    }
}
