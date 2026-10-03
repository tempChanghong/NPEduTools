using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

internal static class ScreenPairingAcceptance
{
    // Only a freshly created loopback fixture is accepted. No actual school/device is used.
    public static async Task<int> RunAsync(string fixturePath, string directory)
    {
        try
        {
            var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(fixturePath));
            string origin = NpepApi.ValidateOrigin(fixture.Text("origin"));
            if (!new Uri(origin).IsLoopback || fixture.Text("kind") != "SCREEN_PAIRING_DISPOSABLE_DATABASE" || Directory.Exists(directory))
                throw new NpepException("ISOLATED_FIXTURE_REQUIRED");
            using (var device = new NpepDevice(directory))
            {
                var info = await device.InspectServerAsync(origin);
                await device.ClaimScreenPairingAsync(origin, info, "原生网页配对验收", "acceptance", fixture.Text("userCode"));
                var view = await device.PollApprovalAsync();
                var approval = (JsonObject)view["approval"]!;
                if (view.Text("state") != "APPROVED" || approval.Text("screenBindingId") != fixture.Text("screenBindingId"))
                    throw new NpepException("ASSIGNMENT_MISMATCH");
                await device.ConfirmAsync(approval.Text("approvalId"));
            }
            using (var restarted = new NpepDevice(directory))
            {
                if (restarted.View().Text("state") != "ACTIVE") throw new NpepException("CREDENTIAL_RECOVERY_FAILED");
                await restarted.OpenSessionAsync();
                await restarted.ReportAsync(NpepStatusReader.Map(null, "acceptance"), 0);
                if (await restarted.UnpairAsync() != "REVOKED") throw new NpepException("REVOKE_FAILED");
            }
            Console.WriteLine("PASS SCREEN PAIRING: claim, fixed assignment, confirmation, DPAPI restart, status, revoke");
            return 0;
        }
        catch (NpepException error) { Console.Error.WriteLine("FAIL SCREEN PAIRING: " + error.Code); return 1; }
        catch (Exception error) { Console.Error.WriteLine("FAIL SCREEN PAIRING: " + error.GetType().Name); return 1; }
    }
}
