#nullable enable
using dotnetCampus.Ipc.CompilerServices.Attributes;
using NPEduTools.Integrations.ClassIsland;

// alpha410's shape generator emits AssemblyIpcProxy, but its runtime factory only
// scans AssemblyIpcProxyJoint. Bridge that pinned-version mismatch explicitly.
// The shape is client-only: the factory ignores JointType for IpcShape classes.
// Remove this bridge only after upgrading AND rerunning the real IPC regression tests.
[assembly: AssemblyIpcProxyJoint(typeof(StrictLessonsShape), typeof(__StrictLessonsShapeIpcProxy), typeof(object))]
[assembly: AssemblyIpcProxyJoint(typeof(StrictProfileShape), typeof(__StrictProfileShapeIpcProxy), typeof(object))]
