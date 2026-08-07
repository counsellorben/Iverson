### Task 1: Strip nav properties from write payloads

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs:18-40`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Produces: the `navIsDistinctKey` local in `ValidateAndNormalizeRelations`'s loop, which Task 2 passes to both per-kind methods.

- [ ] **Step 1: Add `RemoveField` to `StructFieldAccess`**

Place it directly after `SetField`, which it mirrors:

```csharp
    /// <summary>
    /// Removes <paramref name="canonicalName"/> and every case variant of it. Mirrors
    /// <see cref="SetField"/>'s variant handling: clients send camelCase while schemas declare
    /// PascalCase, so removing only the declared spelling would leave the camelCase key behind.
    /// </summary>
    public static void RemoveField(Struct s, string canonicalName)
    {
        foreach (var candidate in Candidates(canonicalName))
            s.Fields.Remove(candidate);
    }
```

- [ ] **Step 2: Compute the name guard and strip in the top-level loop**

In `ValidateAndNormalizeRelations`, inside `foreach (var relation in schema.Relations)`, add the guard before the `switch` and the strip after it. The `switch` body is unchanged.

```csharp
        foreach (var relation in schema.Relations)
        {
            // When PropertyName and ForeignKey collide — Python, TypeScript and Java can all
            // produce that for ManyToMany — the "nav property" and the foreign key are the SAME
            // payload key. There is no separate object to strip, and stripping would delete the
            // foreign key itself.
            var navIsDistinctKey = !string.Equals(
                relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase);

            switch (relation.Kind)
            {
                // ... unchanged ...
            }

            // The stores already ignore nav properties; removing them here keeps them out of the
            // Kafka event body, which is the only place they were still observable.
            if (navIsDistinctKey)
                StructFieldAccess.RemoveField(payload, relation.PropertyName);
        }
```

The per-kind methods need no change in this task. In the collision case with the FK absent, the FK and nav lookups hit the same absent key, so existing behavior is already correct; the guard only becomes load-bearing for the nav *read* in Task 2.

- [ ] **Step 3: Preserve the nav properties the Mapping service echoes back**

`ObjectMappingGrpcService.Post` and `Update` return `Data = request.Payload` (`:324`, `:377`) — the
same `Struct` the validator strips in place — and `EntityCoordinator.PostMappedAsync` /
`UpdateMappedAsync` deserialize that straight back into the caller's entity. Without this step,
stripping silently nulls the caller's nav property. `PersistResponse` carries no payload, so the
Persistence handlers need no equivalent.

Add two file-local helpers to `ObjectMappingGrpcService` rather than duplicating the loop in both
handlers:

```csharp
    // The write handlers echo request.Payload back to the caller, and RelationValidator strips
    // relation properties from it in place. Capture before validating, restore after serialising,
    // so the response keeps the shape the caller sent.
    private static List<(string Name, Value Value)> CaptureNavProperties(
        Struct payload, SchemaDescriptor schema) =>
        schema.Relations
            .Where(r => !string.Equals(r.PropertyName, r.ForeignKey, StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.PropertyName, Value: StructFieldAccess.GetFieldValue(payload, r.PropertyName)))
            .Where(x => x.Value is not null)
            .Select(x => (x.PropertyName, x.Value!))
            .ToList();

    private static void RestoreNavProperties(Struct payload, List<(string Name, Value Value)> captured)
    {
        foreach (var (name, value) in captured)
            StructFieldAccess.SetField(payload, name, value);
    }
```

In **both** `Post` and `Update`, capture immediately before the `ValidateAndNormalizeRelations` call
and restore immediately after the `SerializePayload` call — the FK the validator normalized must
already be in `payloadJson` before anything is put back.

- [ ] **Step 4: Add tests**

Append to `RelationValidatorTests.cs`, following the file's existing style:

1. `ManyToOne_NavPropertyStrippedAndForeignKeySurvives` — `Author` embedded reference with a null FK; assert `AuthorId` present with the nested key and `payload.Fields.Should().NotContainKey("Author")`.
2. `ManyToOne_CamelCaseNavPropertyStripped` — payload carries `author` (camelCase); assert neither `author` nor `Author` remains.
3. `ManyToMany_NavListStripped` — `Tags` list of references; assert `TagIds` populated and `Tags` gone.
4. `OneToMany_NavPropertyStripped` — `Author` list on a `OneToMany` relation; assert it is removed and no FK is written.
5. `PropertyNameEqualsForeignKey_KeyNotStripped` — schema with `RelationDescriptor("TagIds", ManyToMany, "Tag", "TagIds")` and a payload carrying `TagIds` as a GUID list; assert `TagIds` **survives** with its values intact. This is the A7 regression guard: without the name guard the FK is deleted.

And to `ObjectMappingGrpcServiceTests.cs`:

6. `MappingPost_NavPropertyPresentInResponsePayload` — Post an entity carrying a nav property; assert `response.Data` still contains it, while the payload JSON that reached the outbox does not.

- [ ] **Step 5: Run the suite**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Expect **633 passed / 0 failed** (627 baseline + 6). Run it in the foreground and wait for it to finish.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "strip relation nav properties from write payloads"
```

---

