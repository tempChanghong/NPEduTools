#nullable enable
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using dotnetCampus.Ipc.CompilerServices.Attributes;

namespace NPEduTools.Integrations.ClassIsland;

[IpcShape(typeof(IPublicProfileService), IgnoresIpcException = false, Timeout = 2000)]
public class StrictProfileShape : IPublicProfileService
{
    [IpcProperty(IgnoresIpcException = false, Timeout = 2000)]
    public Profile Profile { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public string CurrentProfilePath { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public bool IsCurrentProfileTrusted => throw new NotSupportedException();
    public void SaveProfile() => throw new NotSupportedException();
    public void SaveProfile(string filename) => throw new NotSupportedException();
    public Guid? CreateTempClassPlan(Guid id, Guid? timeLayoutId = null, DateTime? enableDateTime = null) => throw new NotSupportedException();
    public Guid? CreateTempClassPlan(Guid id, Guid? timeLayoutId, DateTime? enableDateTime, bool createTempTimeLayout) => throw new NotSupportedException();
    public void ClearTempClassPlan() => throw new NotSupportedException();
    public void ConvertToStdClassPlan() => throw new NotSupportedException();
    public void ConvertToStdClassPlan(Guid id) => throw new NotSupportedException();
    public void SetupTempClassPlanGroup(Guid key, DateTime? expireTime = null) => throw new NotSupportedException();
    public void ClearTempClassPlanGroup() => throw new NotSupportedException();
}
