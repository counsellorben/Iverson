package io.iverson.client.core;

import com.google.protobuf.Struct;
import com.google.protobuf.Value;

import java.lang.reflect.Field;
import java.time.OffsetDateTime;
import java.time.format.DateTimeFormatter;
import java.util.HashMap;
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
                f.set(instance, fromValue(entry.getValue(), f.getType()));
            }

            return instance;
        } catch (Exception e) {
            throw new RuntimeException("Failed to convert Struct to " + type.getSimpleName(), e);
        }
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
        // Fallback: toString
        return Value.newBuilder().setStringValue(val.toString()).build();
    }

    private static Object fromValue(Value value, Class<?> targetType) {
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
            case BOOL_VALUE   -> value.getBoolValue();
            default           -> null;
        };
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
