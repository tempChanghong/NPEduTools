using System.Runtime.ExceptionServices;

namespace NPEduTools.Host;

// Only explicit Windows endpoint-removal evidence is recoverable. Other metadata failures
// retain the existing fail-closed calibration and capture-cleanup behavior.
internal sealed class NoiseDeviceDisconnectedException(Exception? inner = null)
    : IOException("所选麦克风已断开，等待原设备重新连接。", inner)
{
    internal static bool IsDisconnect(Exception? error) => error is NoiseDeviceDisconnectedException ||
        error?.HResult is unchecked((int)0x88890004) or unchecked((int)0x8007048F) or unchecked((int)0x80070490);
}

internal static class NoiseCaptureCleanup
{
    internal static void Release(Action stop, Action releaseCapture, Action flush, Action releaseEndpoint)
    {
        Exception? failure = null;
        void Run(Action action, bool endpoint)
        {
            try { action(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!endpoint || !NoiseDeviceDisconnectedException.IsDisconnect(error)) failure ??= error;
            }
        }
        Run(stop, true); Run(releaseCapture, true); Run(flush, false); Run(releaseEndpoint, true);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
