package io.iverson.client.core;

import com.google.protobuf.ListValue;
import com.google.protobuf.Struct;
import com.google.protobuf.Value;
import io.iverson.client.annotations.ManyToMany;
import io.iverson.client.annotations.ManyToOne;
import io.iverson.client.annotations.OneToMany;
import io.iverson.client.annotations.OneToOne;

import java.lang.reflect.Field;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.Type;
import java.time.OffsetDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Collection;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/**
 * Converts Java objects to and from {@link Struct} proto messages.
 *
 * <p>Field names are mapped using PascalCase (UpperFirst) so that the server's
 * case-sensitive json_populate_record matches correctly.</p>
 */
public final class StructConverter {

    private StructConverter() {}

    /**
     * Converts a Java object to a {@link Struct} with PascalCase field names.
     */
    public static Struct toStruct(Object entity) {
        Struct.Builder builder = Struct.newBuilder();
        Class<?> cls = entity.getClass();

        for (Field field : getAllFields(cls)) {
            if (isNavigationProperty(field)) continue;
            field.setAccessible(true);
            try {
                Object val = field.get(entity);
                String key = toPascalCase(field.getName());
                builder.putFields(key, toValue(val));
            } catch (IllegalAccessException e) {
                // skip inaccessible fields
            }
        }

        return builder.build();
    }

    /**
     * Converts a {@link Struct} back to a Java object of the given type.
     * Field names in the struct are expected to be PascalCase; the converter
     * matches them case-insensitively to Java camelCase field names.
     */
    public static <T> T fromStruct(Struct struct, Class<T> type) {
        try {
            T instance = type.getDeclaredConstructor().newInstance();
            Map<String, Value> fields = struct.getFieldsMap();

            // Build a lookup: lowercase(pascalCase) -> field
            Map<String, Field> fieldMap = new HashMap<>();
            for (Field f : getAllFields(type)) {
                fieldMap.put(toPascalCase(f.getName()).toLowerCase(), f);
            }

            for (Map.Entry<String, Value> entry : fields.entrySet()) {
                Field f = fieldMap.get(entry.getKey().toLowerCase());
                if (f == null) continue;
                f.setAccessible(true);
                f.set(instance, fromValue(entry.getValue(), f.getType(), f.getGenericType()));
            }

            return instance;
        } catch (Exception e) {
            throw new RuntimeException("Failed to convert Struct to " + type.getSimpleName(), e);
        }
    }

    /**
     * Converts a {@link Struct} to a plain {@code Map<String, Object>}, one entry per field,
     * keyed by the struct's raw (PascalCase) field names. Used for untyped result rows — e.g.
     * GroupBy/Pipeline output — where no target class is known ahead of time.
     */
    public static Map<String, Object> fromStructAsMap(Struct struct) {
        Map<String, Object> result = new HashMap<>();
        for (Map.Entry<String, Value> entry : struct.getFieldsMap().entrySet()) {
            result.put(entry.getKey(), fromValue(entry.getValue()));
        }
        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    static String toPascalCase(String camelCase) {
        if (camelCase == null || camelCase.isEmpty()) return camelCase;
        return Character.toUpperCase(camelCase.charAt(0)) + camelCase.substring(1);
    }

    private static Value toValue(Object val) {
        if (val == null) return Value.newBuilder().setNullValue(com.google.protobuf.NullValue.NULL_VALUE).build();
        if (val instanceof String s)  return Value.newBuilder().setStringValue(s).build();
        if (val instanceof UUID u)    return Value.newBuilder().setStringValue(u.toString()).build();
        if (val instanceof Boolean b) return Value.newBuilder().setBoolValue(b).build();
        if (val instanceof Number n)  return Value.newBuilder().setNumberValue(n.doubleValue()).build();
        if (val instanceof OffsetDateTime dt)
            return Value.newBuilder().setStringValue(dt.format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)).build();
        if (val instanceof Collection<?> coll) {
            ListValue.Builder listBuilder = ListValue.newBuilder();
            for (Object element : coll) {
                listBuilder.addValues(toValue(element));
            }
            return Value.newBuilder().setListValue(listBuilder.build()).build();
        }
        // Fallback: toString
        return Value.newBuilder().setStringValue(val.toString()).build();
    }

    /**
     * A field is a navigation property — and therefore omitted from the write
     * payload — when it carries a relation annotation ({@link ManyToOne},
     * {@link OneToOne}, {@link ManyToMany}, {@link OneToMany}) and its declared
     * type is an entity, or a {@link Collection} of entities. The foreign key
     * itself lives in a separate, unannotated field beside it (e.g. {@code
     * authorId}, {@code tagIds}) and is serialized normally.
     */
    private static boolean isNavigationProperty(Field field) {
        boolean hasRelationAnnotation =
            field.isAnnotationPresent(ManyToOne.class) ||
            field.isAnnotationPresent(OneToOne.class) ||
            field.isAnnotationPresent(ManyToMany.class) ||
            field.isAnnotationPresent(OneToMany.class);
        if (!hasRelationAnnotation) return false;

        Class<?> fieldType = field.getType();
        if (Collection.class.isAssignableFrom(fieldType)) {
            Type genericType = field.getGenericType();
            if (genericType instanceof ParameterizedType pt) {
                Type[] typeArgs = pt.getActualTypeArguments();
                if (typeArgs.length == 1 && typeArgs[0] instanceof Class<?> elementType) {
                    return elementType.isAnnotationPresent(io.iverson.client.annotations.IversonEntity.class);
                }
            }
            return false;
        }

        return fieldType.isAnnotationPresent(io.iverson.client.annotations.IversonEntity.class);
    }

    /** Untyped per-kind unwrapping, used by {@link #fromStructAsMap(Struct)}. */
    private static Object fromValue(Value value) {
        return switch (value.getKindCase()) {
            case STRING_VALUE -> value.getStringValue();
            case NUMBER_VALUE -> value.getNumberValue();
            case BOOL_VALUE   -> value.getBoolValue();
            case LIST_VALUE   -> {
                List<Object> items = new ArrayList<>();
                for (Value element : value.getListValue().getValuesList()) {
                    items.add(fromValue(element));
                }
                yield items;
            }
            default           -> null;
        };
    }

    private static Object fromValue(Value value, Class<?> targetType, Type genericType) {
        if (value.getKindCase() == Value.KindCase.LIST_VALUE) {
            if (!Collection.class.isAssignableFrom(targetType)) return null;
            Class<?> elementType = elementTypeOf(genericType);
            if (elementType == null) return null;
            List<Object> items = new ArrayList<>();
            for (Value element : value.getListValue().getValuesList()) {
                items.add(fromValue(element, elementType, null));
            }
            return items;
        }
        return switch (value.getKindCase()) {
            case STRING_VALUE -> {
                String s = value.getStringValue();
                if (targetType == UUID.class) yield UUID.fromString(s);
                if (targetType == OffsetDateTime.class) yield OffsetDateTime.parse(s);
                yield s;
            }
            case NUMBER_VALUE -> {
                double d = value.getNumberValue();
                if (targetType == int.class || targetType == Integer.class) yield (int) d;
                if (targetType == long.class || targetType == Long.class) yield (long) d;
                if (targetType == float.class || targetType == Float.class) yield (float) d;
                yield d;
            }
            case BOOL_VALUE     -> value.getBoolValue();
            // Only recurse into fromStruct when the target is itself a registered Iverson
            // entity (parity with Python's relation["kind"] gate, TypeScript's rel.kind gate,
            // and Go's fm.RelationKind gate). An unannotated scalar field whose PascalCase name
            // happens to collide with a nav property (e.g. `javaAuthor: UUID` alongside
            // `javaAuthorId`) would otherwise try `UUID.class.getDeclaredConstructor()` and
            // fail the whole read where it previously just yielded null.
            case STRUCT_VALUE   -> targetType.isAnnotationPresent(io.iverson.client.annotations.IversonEntity.class)
                ? fromStruct(value.getStructValue(), targetType)
                : null;
            default             -> null;
        };
    }

    /** The single type argument of a parameterized collection, or null when not resolvable. */
    private static Class<?> elementTypeOf(Type genericType) {
        if (genericType instanceof ParameterizedType pt) {
            Type[] args = pt.getActualTypeArguments();
            if (args.length == 1 && args[0] instanceof Class<?> element) return element;
        }
        return null;
    }

    private static java.util.List<Field> getAllFields(Class<?> cls) {
        java.util.List<Field> fields = new java.util.ArrayList<>();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                // skip synthetic fields (e.g. $jacocoData)
                if (!f.isSynthetic()) fields.add(f);
            }
            cls = cls.getSuperclass();
        }
        return fields;
    }
}
