package io.iverson.conformance;

import com.google.protobuf.util.JsonFormat;
import io.grpc.CallOptions;
import io.grpc.Channel;
import io.grpc.ClientCall;
import io.grpc.ClientInterceptor;
import io.grpc.ForwardingClientCall;
import io.grpc.MethodDescriptor;
import iverson.ObjectMapping.SchemaRequest;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The sanctioned seam for observing what the client actually sends: {@code SchemaRegistrar}
 * reads the package-private {@code IversonClient.mappingStub}, so there is no stub to wrap
 * directly as in the Python/Go drivers. Instead this wraps the whole channel via
 * {@code ManagedChannelBuilder.intercept(...)}, records the outgoing {@code SchemaRequest}'s
 * root type for every type sent, and forwards every call unchanged. Nothing is judged here —
 * the JSON is reported verbatim.
 */
final class CaptureInterceptor implements ClientInterceptor {

    private static final JsonFormat.Printer PRINTER =
        JsonFormat.printer().includingDefaultValueFields();

    private final Map<String, String> captured = new LinkedHashMap<>();

    /**
     * The descriptor for the first of {@code preferredTypeNames} that was actually sent under
     * that exact name, or null if none of them was. Never substitutes a different type's
     * descriptor: each register step reports one named type, and a wrong-but-present descriptor
     * would have the orchestrator re-register the wrong schema.
     */
    String select(String... preferredTypeNames) {
        for (String preferred : preferredTypeNames) {
            if (preferred == null || preferred.isEmpty()) continue;
            for (Map.Entry<String, String> entry : captured.entrySet()) {
                if (entry.getKey().equalsIgnoreCase(preferred)) return entry.getValue();
            }
        }
        return null;
    }

    @Override
    public <ReqT, RespT> ClientCall<ReqT, RespT> interceptCall(
            MethodDescriptor<ReqT, RespT> method, CallOptions callOptions, Channel next) {
        return new ForwardingClientCall.SimpleForwardingClientCall<>(next.newCall(method, callOptions)) {
            @Override
            public void sendMessage(ReqT message) {
                if (message instanceof SchemaRequest request && request.hasRootType()) {
                    try {
                        captured.put(
                            request.getRootType().getTypeName(),
                            PRINTER.print(request.getRootType()));
                    } catch (Exception ignored) {
                        // Best-effort capture only; never block the outgoing call on a
                        // formatting failure — the RPC itself is the observation of record.
                    }
                }
                super.sendMessage(message);
            }
        };
    }
}
