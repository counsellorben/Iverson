using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S9 <c>error-contract</c>'s unregistered fixture: declared by all five drivers through their own
/// client libraries and registered by NOTHING — no driver, no scenario, no orchestrator, in this
/// run or any other. A mapped write against it must be refused with <c>FailedPrecondition</c>
/// (<c>ObjectMappingGrpcService.RequireSchema</c>), which is the whole observation.
///
/// <para><b>Do not register this type.</b> Every register phase in this driver sets
/// <c>capture.OnlySendTypeName</c> before calling <c>SchemaRegistrar.RegisterAllAsync()</c>, so the
/// registrar's walk over the whole assembly's <c>[IversonEntity]</c> types never actually sends
/// this one. Removing that guard from any register phase would register it as a side effect and
/// silently turn <c>IVC-ERR-005</c> green for the wrong reason — the write would then be accepted,
/// which the assertion reads as a failure, so the harness would go red rather than lie, but the
/// fixture would be gone.</para>
/// </summary>
[IversonEntity]
public class ErrorUnregisteredDoc
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
