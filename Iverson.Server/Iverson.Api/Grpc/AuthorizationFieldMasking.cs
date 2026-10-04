using System.Security.Claims;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

internal static class AuthorizationFieldMasking
{
    /// <summary>
    /// Shared write-path authorization gate for Post/Update on both ObjectMapping and
    /// ObjectPersistence services: evaluates row+field authorization for the acting user,
    /// denies/throws as appropriate, force-sets or validates the owner field, rejects any field
    /// the caller isn't allowed to write, and — on Update — carries forward the stored values of
    /// the fields the caller isn't allowed to write and left out (<see cref="CarryForwardRestrictedFields"/>)
    /// and of the owner column if it was left out.
    /// <para>
    /// Also runs <paramref name="payloadSizeValidator"/>'s text-column-size guard, but only as the
    /// LAST step, after every authorization-adjacent check in this method (the initial denial
    /// check, and — on the existing-row/Update branch — TenantMismatch, TenantImmutable,
    /// OwnerMismatch, OwnerImmutable, and <see cref="RejectDisallowedFields"/>). Denial-first: a
    /// caller who fails ANY of those checks never reaches the size guard's <c>InvalidArgument</c>
    /// at all — most of them throw <c>PermissionDenied</c> (with its audit trail entry), and
    /// <see cref="RejectDisallowedFields"/> throws its own <c>InvalidArgument</c> for a field the
    /// caller may never write — but an oversized payload must never let an unauthorized or
    /// cross-tenant caller learn that a row exists, which column overflowed, or its size limit.
    /// Centralizing the call here (rather than at each service's call site) is what makes both
    /// write paths get the guard automatically instead of relying on each RPC to remember to call
    /// it.
    /// </para>
    /// </summary>
    /// <param name="existingRowJson">
    /// JSON of the row being written, or null when there is no pre-existing row. Null on a create
    /// (Post), where ownership is force-set rather than validated; on an Update, null means the
    /// key has no row in the caller's tenant, which <paramref name="requireExistingRow"/> turns
    /// into NotFound.
    /// </param>
    /// <param name="requireExistingRow">
    /// True for Update, which never creates a row: a null <paramref name="existingRowJson"/>
    /// answers NotFound. False for Post.
    /// </param>
    /// <param name="deniedMessage">
    /// Exception message used both when the caller has no access at all and — for the
    /// existing-row branch — when an ownership mismatch is detected. Callers pass the
    /// create- or update-specific wording ("Not authorized to create/update this entity.").
    /// </param>
    /// <returns>
    /// The canonical keys carried forward into <paramref name="payload"/> from the stored row: the
    /// restricted fields <see cref="CarryForwardRestrictedFields"/> inserted, plus the owner column
    /// when the payload left it out. Always empty for a create. A caller that returns the payload
    /// to the client removes these keys from its response.
    /// </returns>
    public static IReadOnlyCollection<string> EnforceWriteAuthorization(
        IRowFieldAuthorizationEvaluator authEvaluator,
        ClaimsPrincipal? actingUser,
        SchemaDescriptor schema,
        Struct payload,
        AuthorizationAction action,
        string deniedMessage,
        string? existingRowJson,
        bool requireExistingRow,
        AuditLog auditLog,
        IPayloadSizeValidator payloadSizeValidator)
    {
        // The RPC, not the row: a denied Update of a key with no stored row is still an Update.
        var auditAction = requireExistingRow || existingRowJson is not null ? "Update" : "Create";
        var resourceKey = StructFieldAccess.GetFieldString(payload, schema.KeyColumn.Name);

        // FIRST — and the required position is BEFORE THE PermissionDenied THROW below, not
        // merely "before Evaluate": authEvaluator.Evaluate does not throw, so moving this check to
        // sit between Evaluate and the `if (decision.Denied)` block is behaviourally identical and
        // no test can tell the difference (a mutation doing exactly that survives the whole suite).
        // Only sinking it BELOW the throw changes behaviour, and that is what
        // EnforceWriteAuthorization_DeniedCallerSmugglingTheTenantColumn_StillGetsInvalidArgument
        // pins. Decision 5: the server-owned tenant column is rejected on the way in, never
        // silently overwritten. This is a MALFORMED-REQUEST check and is independent of identity,
        // so it must not be reachable only for authorized callers — placed after the throw, an
        // unauthorized caller smuggling the column would get PermissionDenied, which masks the
        // malformed field entirely and makes the InvalidArgument contract conditional on
        // authorization.
        // Distinct from the tenant-immutability check further down, which compares a DECLARED
        // tenant field's value and runs on the update branch only; this one is unconditional and
        // covers create and update alike. Case-insensitive via SchemaDescriptor.IsTenantColumn, so
        // a re-cased "__tenantid" cannot slip through (SetAuthoritativeField's canonical-casing
        // fixup would otherwise absorb it silently).
        var smuggled = payload.Fields.Keys.FirstOrDefault(SchemaDescriptor.IsTenantColumn);
        if (smuggled is not null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Payload for '{schema.TypeName}' carries '{smuggled}', which is a reserved "
                + "server-owned column. The server derives a row's tenant from the acting user's "
                + "identity; remove the field from the payload."));
        }

        var decision = authEvaluator.Evaluate(schema, actingUser, action);
        if (decision.Denied)
        {
            auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "AccessDenied");
            throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
        }

        // Update never creates a row (CSR round 10 Finding #10). AFTER the denial throw, never
        // before it: a caller who is denied anyway must get PermissionDenied whether or not the
        // key exists, or the status would tell them which keys exist. The existing-row read is
        // tenant-scoped, so a key that exists nowhere and a key owned by another tenant are both
        // null here and get the same answer. No audit entry, matching Mapping Get's not-found.
        if (requireExistingRow && existingRowJson is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"'{schema.TypeName}:{resourceKey}' not found."));

        Struct? existingStruct = null;
        if (existingRowJson is null)
        {
            // No pre-existing row: a create (Post). Force-set the tenant column unconditionally
            // (tenant is strictly additive — it applies to bypass callers too, unlike ownership
            // below). Force-set the owner field for ownership-required callers; leave it
            // untouched for bypass callers.
            if (decision.TenantColumn is not null)
                SetAuthoritativeField(payload, decision.TenantColumn, decision.TenantValue!);
            if (decision.OwnershipRequired)
                SetAuthoritativeField(payload, decision.OwnerFieldName!, decision.OwnerValue!);
        }
        else
        {
            existingStruct = JsonParser.Default.Parse<Struct>(existingRowJson);
            PreserveExactIntegers(existingStruct, existingRowJson);

            // Tenant match + immutability are unconditional — they apply even to bypass
            // callers, unlike the ownership check below.
            if (decision.TenantColumn is not null)
            {
                // Unreachable today (CSR round 9 Finding #5): both Update call sites now narrow
                // their existing-row read to the acting tenant, so existingRowJson never carries a
                // foreign tenant's value here. Kept as a fail-closed backstop for any future or
                // out-of-band caller that passes a foreign-tenant existingRowJson — do not delete.
                if (StructFieldAccess.GetFieldString(existingStruct, decision.TenantColumn) != decision.TenantValue)
                {
                    auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "TenantMismatch");
                    throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
                }
                var attemptedTenant = StructFieldAccess.GetFieldString(payload, decision.TenantColumn);
                if (attemptedTenant is not null && attemptedTenant != decision.TenantValue)
                {
                    auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "TenantImmutable");
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant field is immutable."));
                }

                // Force-set AFTER the two checks above have passed, so this can only ever write
                // back the value the row already carries. It is not redundant: the tenant column
                // is server-owned, so a client payload never carries it — without this the
                // serialized payload published to Kafka would have no tenant value at all, and
                // EngagementRepository.UpsertAsync (StarRocks Primary Key model: an INSERT of an
                // existing key is a FULL-ROW REPLACE) would reset the projected row's tenant
                // column to NULL, silently emptying every subsequent StarRocks read for that
                // tenant. The create branch above already force-sets it for the same reason.
                SetAuthoritativeField(payload, decision.TenantColumn, decision.TenantValue!);
            }

            if (decision.OwnershipRequired &&
                StructFieldAccess.GetFieldString(existingStruct, decision.OwnerFieldName!) != decision.OwnerValue)
            {
                auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "OwnerMismatch");
                throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
            }

            // The owner field name for immutability purposes is sourced from the schema's
            // declared Authorization.OwnerField, NEVER from decision.OwnerFieldName — the
            // latter is null for bypass callers, who must still be blocked from reassigning
            // ownership of an existing row.
            var ownerFieldName = schema.Authorization?.OwnerField;
            if (!string.IsNullOrEmpty(ownerFieldName))
            {
                var attemptedOwnerValue = StructFieldAccess.GetFieldString(payload, ownerFieldName);
                if (attemptedOwnerValue is not null &&
                    attemptedOwnerValue != StructFieldAccess.GetFieldString(existingStruct, ownerFieldName))
                {
                    auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "OwnerImmutable");
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Owner field is immutable after creation."));
                }
            }
        }

        // The owner and tenant columns are exempt: by this point the server has force-set or
        // verified both. The tenant column has to be, because AllowedFields never lists it (it is not
        // permissionable), so without the exemption every field-restricted write to a schema with
        // the reserved tenant column failed here on the column the server itself had just set.
        RejectDisallowedFields(payload, decision.AllowedFields,
            new[] { decision.OwnerFieldName, decision.TenantColumn }.OfType<string>().ToList());

        // Update branch only. AFTER RejectDisallowedFields, so a restricted field the caller did
        // send is still an error, and a carried-in owner is never mistaken for one the caller sent;
        // BEFORE the size guard, so the guard sees the payload as written.
        var carriedForward = new List<string>();
        if (existingStruct is not null)
        {
            carriedForward.AddRange(CarryForwardRestrictedFields(payload, existingStruct, decision.AllowedFields));

            // The owner column too, for every caller: sourced from the schema, not the decision,
            // whose OwnerFieldName is null for bypass callers. The column is writable, so the
            // restricted-field rule never reaches it, and OwnerImmutable only compares an owner
            // that is present — an Update that omits it would write the row with a NULL owner,
            // orphaning it from its owner.
            var ownerField = schema.Authorization?.OwnerField;
            var storedOwnerKey = ownerField is null ? null : existingStruct.Fields.Keys.FirstOrDefault(
                k => string.Equals(k, ownerField, StringComparison.OrdinalIgnoreCase));
            if (storedOwnerKey is not null && CarryForward(payload, existingStruct, storedOwnerKey))
                carriedForward.Add(storedOwnerKey);
        }

        // LAST — after every check above (the initial denial check and, on the existing-row/Update
        // branch, TenantMismatch/TenantImmutable/OwnerMismatch/OwnerImmutable/RejectDisallowedFields),
        // whether that check throws PermissionDenied (most of them, with its audit-logged denial
        // reason) or InvalidArgument (RejectDisallowedFields, for a field the caller may never
        // write). A cross-tenant or otherwise unauthorized caller must never reach this guard even
        // when their payload is also oversized; running the size check any earlier would let such a
        // caller learn (via InvalidArgument naming a column and its size limit) that the row exists,
        // before authorization has even decided they may see it.
        payloadSizeValidator.ValidateTextColumnSizes(payload, schema);

        return carriedForward;
    }

    /// <summary>
    /// Writes a server-computed, authoritative column (tenant or owner) into the payload under the
    /// schema's canonical casing, first dropping any client-supplied key that differs only by case.
    /// <para>
    /// The .NET client serializes with a camelCase naming policy, so a payload arrives with
    /// <c>tenantId</c>/<c>ownerId</c> while the schema column is <c>TenantId</c>/<c>OwnerId</c>.
    /// Setting the canonical key without removing the camelCase one leaves BOTH in the Struct, and
    /// <see cref="StructSerializer.SerializePayload"/> then UpperFirst()es every key into a
    /// Dictionary — throwing "An item with the same key has already been added. Key: TenantId" and
    /// failing the write with a bare Unknown status. The server-computed value must win: it is
    /// derived from the caller's token, not from client-supplied data.
    /// </para>
    /// </summary>
    private static void SetAuthoritativeField(Struct payload, string canonicalName, string value) =>
        StructFieldAccess.SetField(payload, canonicalName, Value.ForString(value));

    /// <summary>
    /// Removes every key naming the server-owned tenant column, in any casing. Unconditional and
    /// independent of any allow-list: the column is never client-addressable, so it is never
    /// something a caller can be granted.
    /// <para>
    /// Keyed on <see cref="SchemaDescriptor.TenantColumnName"/> — the reserved spelling — NOT on
    /// a schema's <c>TenantColumn</c>. A legacy schema whose boundary sits on a client-declared
    /// column (e.g. <c>TenantId</c>) has always exposed that name as part of the client's own
    /// contract, and stripping it would silently drop a field the client declared.
    /// </para>
    /// </summary>
    public static void RemoveTenantColumn(Struct payload)
    {
        var toRemove = payload.Fields.Keys
            .Where(SchemaDescriptor.IsTenantColumn)
            .ToList();
        foreach (var key in toRemove)
            payload.Fields.Remove(key);
    }

    /// <summary>
    /// Row-dictionary counterpart of <see cref="RemoveTenantColumn(Struct)"/>, for the four
    /// streaming SQL RPCs (Search, GroupBy, Pipeline, MatchPattern) that build a response from the StarRocks row
    /// dictionary and never reach <see cref="MaskDisallowedFields"/>. Same reserved-name rule, so
    /// the decision of WHICH name is server-owned stays defined in exactly one place.
    /// </summary>
    public static void RemoveTenantColumn(IDictionary<string, object?> row)
    {
        var toRemove = row.Keys
            .Where(SchemaDescriptor.IsTenantColumn)
            .ToList();
        foreach (var key in toRemove)
            row.Remove(key);
    }

    public static void MaskDisallowedFields(
        Struct payload,
        IReadOnlySet<string>? allowedFields,
        string? exemptField = null)
    {
        // BEFORE the early return, deliberately. A schema with no field permissions produces a
        // null allowedFields, and that is exactly the case in which the guard below would let the
        // server-owned tenant column straight through to the caller.
        RemoveTenantColumn(payload);

        if (allowedFields is null) return;

        var toRemove = payload.Fields.Keys
            .Where(key => !allowedFields.Contains(StructSerializer.UpperFirst(key)) && StructSerializer.UpperFirst(key) != exemptField)
            .ToList();
        foreach (var key in toRemove)
            payload.Fields.Remove(key);
    }

    public static void RejectDisallowedFields(
        Struct payload,
        IReadOnlySet<string>? allowedFields,
        IReadOnlyCollection<string> exemptFields)
    {
        if (allowedFields is null) return;

        var disallowed = payload.Fields.Keys
            .Select(StructSerializer.UpperFirst)
            .Where(canonical => !allowedFields.Contains(canonical) && !exemptFields.Contains(canonical))
            .ToList();
        if (disallowed.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Field(s) not permitted for this caller: {string.Join(", ", disallowed)}"));
    }

    /// <summary>
    /// Copies into <paramref name="payload"/> the stored value of every field the caller may not
    /// write and did not send, and returns the keys it inserted.
    /// <para>
    /// An update is a full-row replace in Postgres (<c>OutboxWriter</c>'s upsert sets every column)
    /// and in StarRocks (a Primary Key insert), so a field missing from the payload is cleared. For
    /// a field the caller may write that is the caller's choice; for one it may not write, it was
    /// a way to erase a value the caller was never allowed to change.
    /// </para>
    /// <para>
    /// <paramref name="existingRow"/> is the stored row's <c>row_to_json</c>, so its keys are the
    /// canonical column names and it holds restricted columns too. A payload key matching a stored
    /// key in any casing counts as sent: the client serializes camelCase, and inserting the
    /// canonical key beside it would leave two keys that <c>SerializePayload</c> folds into one.
    /// A no-op when <paramref name="allowedFields"/> is null (no field restriction).
    /// </para>
    /// </summary>
    public static IReadOnlyCollection<string> CarryForwardRestrictedFields(
        Struct payload,
        Struct existingRow,
        IReadOnlySet<string>? allowedFields)
    {
        if (allowedFields is null) return [];

        var inserted = new List<string>();
        foreach (var storedKey in existingRow.Fields.Keys)
            if (!allowedFields.Contains(StructSerializer.UpperFirst(storedKey)) &&
                CarryForward(payload, existingRow, storedKey))
                inserted.Add(storedKey);
        return inserted;
    }

    /// <summary>
    /// Copies <paramref name="existingRow"/>'s <paramref name="storedKey"/> into
    /// <paramref name="payload"/> under that canonical name, unless the stored row lacks it or the
    /// payload already carries it in any casing. Returns whether it copied.
    /// </summary>
    private static bool CarryForward(Struct payload, Struct existingRow, string storedKey)
    {
        if (!existingRow.Fields.TryGetValue(storedKey, out var storedValue)) return false;
        if (payload.Fields.Keys.Any(k => string.Equals(k, storedKey, StringComparison.OrdinalIgnoreCase))) return false;

        payload.Fields[storedKey] = storedValue;
        return true;
    }

    /// <summary>
    /// A Struct number is a double, so an integer outside ±2^53 in the stored row would come back
    /// rounded. Such a value (or array element) is carried as its exact JSON text instead, which
    /// json_populate_record parses back into BIGINT exactly.
    /// </summary>
    private static void PreserveExactIntegers(Struct row, string rowJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rowJson);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Number && !IsDoubleExact(p.Value))
                row.Fields[p.Name] = Value.ForString(p.Value.GetRawText());
            else if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Array &&
                     p.Value.EnumerateArray().Any(e => e.ValueKind == System.Text.Json.JsonValueKind.Number && !IsDoubleExact(e)))
                row.Fields[p.Name] = Value.ForList(p.Value.EnumerateArray().Select(e =>
                    e.ValueKind == System.Text.Json.JsonValueKind.Null ? Value.ForNull()
                    : IsDoubleExact(e) ? Value.ForNumber(e.GetDouble()) : Value.ForString(e.GetRawText())).ToArray());
        }
    }

    private static bool IsDoubleExact(System.Text.Json.JsonElement e) =>
        !e.TryGetInt64(out var l) || (l >= -(1L << 53) && l <= (1L << 53));
}
