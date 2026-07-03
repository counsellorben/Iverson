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

        // Collect nav property field names (annotated with any relation annotation)
        Set<String> navFieldNames = new HashSet<>();
        Field keyField = null;

        for (Field field : getAllFields(cls)) {
            if (field.getAnnotation(IversonKey.class) != null) {
                keyField = field;
            }
            if (isRelationField(field)) {
                navFieldNames.add(field.getName());
            }
        }

        if (keyField == null) {
            throw new IllegalArgumentException(
                cls.getSimpleName() + " has no field annotated with @IversonKey");
        }

        // Key property first
        builder.addProperties(buildKeyDescriptor(keyField));

        // Non-key scalar properties
        for (Field field : getAllFields(cls)) {
            if (field == keyField) continue;
            if (navFieldNames.contains(field.getName())) continue;
            PropertyDescriptor pd = tryBuildPropertyDescriptor(field);
            if (pd != null) builder.addProperties(pd);
        }

        // Relation descriptors
        for (Field field : getAllFields(cls)) {
            if (!isRelationField(field)) continue;
            RelationDescriptor rd = buildRelationDescriptor(field, cls.getSimpleName());
            if (rd != null) builder.addRelations(rd);
        }

        return builder.build();
    }

    private PropertyDescriptor buildKeyDescriptor(Field field) {
        ClrType clrType = detectClrType(field.getType());
        if (clrType == null) clrType = ClrType.CLR_STRING; // fallback
        PropertyDescriptor.Builder b = PropertyDescriptor.newBuilder()
            .setName(StructConverter.toPascalCase(field.getName()))
            .setClrType(clrType)
            .setIsKey(true)
            .setIsNullable(false);
        applyAnnotations(b, field);
        return b.build();
    }

    private PropertyDescriptor tryBuildPropertyDescriptor(Field field) {
        ClrType clrType = detectClrType(field.getType());
        if (clrType == null) return null;

        boolean isNullable = !field.getType().isPrimitive();
        PropertyDescriptor.Builder b = PropertyDescriptor.newBuilder()
            .setName(StructConverter.toPascalCase(field.getName()))
            .setClrType(clrType)
            .setIsKey(false)
            .setIsNullable(isNullable);
        applyAnnotations(b, field);
        return b.build();
    }

    private void applyAnnotations(PropertyDescriptor.Builder b, Field field) {
        IversonSearchKey sk = field.getAnnotation(IversonSearchKey.class);
        if (sk != null) {
            b.setIsSearchKey(true);
            b.setSearchKeyOrder(sk.order());
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
