using System.Reflection;
using VedaCA.Diagnostics.Core;

namespace VedaCA.SoundPreview.Tests;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ASSERT FAILED: " + message);
    }

    private static int Main()
    {
        // Apply this test project first, then run:
        // dotnet run --project tests/VedaCA.SoundPreview.Tests
        // Before the implementation exists, reflection produces a deliberate
        // assertion failure instead of a compilation failure.
        // Apply the implementation afterwards and repeat the same command.
        // This runner uses only injected managed delegates; no OS backend is loaded.
        try
        {
            var type = typeof(ChatLogObservation).Assembly.GetType(
                "VedaCA.Diagnostics.Core.SoundPreviewController", false);
            Require(type is not null, "SoundPreviewController is missing; apply the implementation after this RED assertion.");
            var controllerType = type!;
            var constructor = controllerType.GetConstructor(new[] { typeof(Func<bool>) });
            var preview = controllerType.GetMethod("Preview", Type.EmptyTypes);
            var status = controllerType.GetProperty("Status");
            Require(constructor is not null, "public constructor accepting Func<bool> is required");
            Require(preview is not null && preview.ReturnType == typeof(bool), "public bool Preview() is required");
            Require(status is not null && status.PropertyType == typeof(string) && status.CanRead && !status.CanWrite,
                "public read-only string Status is required");

            object Create(Func<bool> backend) => constructor!.Invoke(new object[] { backend });
            string Read(object controller) => (string)status!.GetValue(controller)!;
            bool Play(object controller) => (bool)preview!.Invoke(controller, null)!;

            var calls = 0;
            var success = Create(() => { calls++; return true; });
            var initial = Read(success);
            Require(initial.Contains("尚未试听"), "initial state visibly says no preview has occurred");
            for (var i = 0; i < 20; i++)
                Require(Read(success) == initial, "state reads are stable");
            Require(calls == 0, "construction and state queries must not play");
            Require(Play(success), "successful backend returns success");
            Require(calls == 1, "one explicit preview calls the backend exactly once");
            var sent = Read(success);
            Require(sent.Contains("请求已发送") && sent.Contains("不保证能听到"), "success describes request acceptance, not audibility");
            for (var i = 0; i < 20; i++)
                Require(Read(success) == sent, "reading success does not change state");
            Require(calls == 1, "state reads must not replay");
            Require(Play(success) && calls == 2, "a second explicit preview calls the backend once more");

            var failedCalls = 0;
            var failure = Create(() => { failedCalls++; return false; });
            Require(failedCalls == 0, "failure backend is not called by construction");
            Require(!Play(failure) && failedCalls == 1, "false backend result is safely reported without retry");
            Require(Read(failure).Contains("失败"), "false backend result has visible failure feedback");
            Require(failedCalls == 1, "failure state query does not retry");

            foreach (var error in new Exception[]
            {
                new InvalidOperationException("synthetic private detail"),
                new PlatformNotSupportedException(),
                new DllNotFoundException(),
                new EntryPointNotFoundException(),
            })
            {
                var thrownCalls = 0;
                var throws = Create(() => { thrownCalls++; throw error; });
                Require(thrownCalls == 0, "throwing backend is not called by construction");
                Require(!Play(throws) && thrownCalls == 1, "backend exception is contained without retry");
                var message = Read(throws);
                Require(message.Contains("失败"), "backend exception has visible failure feedback");
                Require(!message.Contains("synthetic private detail"), "exception details are not exposed");
                Require(thrownCalls == 1, "exception state query does not retry");
            }

            var recoveryCalls = 0;
            var recovery = Create(() => ++recoveryCalls > 1);
            Require(!Play(recovery) && Read(recovery).Contains("失败"), "initial failure is visible");
            Require(Play(recovery) && recoveryCalls == 2 && Read(recovery).Contains("请求已发送"),
                "only a new explicit preview retries and updates feedback");
            Console.WriteLine("PASS: managed SoundPreviewController assertions; injected fake backends only.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }
}
