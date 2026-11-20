using System.Diagnostics;
using NoorVox.Core;
using NoorVox.Core.Workers;

namespace NoorVox.Tests;

/// <summary>Process supervision without a model: start/stop/duplicates/crash/idle/job-kill. Real processes, no mocks.</summary>
public class WorkerLifecycleTests
{
    static WorkerSupervisor Sup(TestHome home) => new(home.Storage(), TestPaths.WorkerExe, "test");

    [Fact]
    public async Task Start_then_graceful_stop_leaves_no_process_and_no_record()
    {
        using var home = new TestHome();
        using var sup = Sup(home);
        var st = await sup.StartAsync("w1", boundToLauncher: false, idleTimeoutSeconds: 0, CancellationToken.None);
        Assert.Equal("idle", st.State);
        Assert.True(TestPaths.ProcessAlive(st.Pid!.Value));
        Assert.Equal("CPU", st.ExecutionProvider);
        Assert.Null(st.LoadedModel);

        var stop = await sup.StopAsync("w1", force: false, TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Equal("graceful", stop.Method);
        Assert.True(await TestPaths.WaitUntil(() => !TestPaths.ProcessAlive(st.Pid!.Value), TimeSpan.FromSeconds(5)));
        Assert.Equal("stopped", (await sup.GetStatusAsync("w1", CancellationToken.None)).State);
        Assert.Empty(sup.KnownWorkerIds());
    }

    [Fact]
    public async Task Duplicate_start_is_refused()
    {
        using var home = new TestHome();
        using var sup = Sup(home);
        await sup.StartAsync("dup", false, 0, CancellationToken.None);
        try
        {
            var ex = await Assert.ThrowsAsync<NoorVoxException>(() => sup.StartAsync("dup", false, 0, CancellationToken.None));
            Assert.Equal(ErrorCodes.WorkerAlreadyRunning, ex.Code);
            Assert.Equal(ExitCode.WorkerBusy, ex.ExitCode);
            Assert.Single(sup.KnownWorkerIds());
        }
        finally { await sup.StopAllAsync(force: true, TimeSpan.FromSeconds(5), CancellationToken.None); }
    }

    [Fact]
    public async Task Externally_killed_worker_is_reported_as_error_then_cleared()
    {
        using var home = new TestHome();
        using var sup = Sup(home);
        var st = await sup.StartAsync("crashy", false, 0, CancellationToken.None);
        using (var p = Process.GetProcessById(st.Pid!.Value)) { p.Kill(); p.WaitForExit(5000); }

        var after = await sup.GetStatusAsync("crashy", CancellationToken.None);
        Assert.Equal("error", after.State);
        Assert.NotNull(after.Error);

        var stop = await sup.StopAsync("crashy", false, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(stop.WasRunning);
        Assert.Equal("stopped", (await sup.GetStatusAsync("crashy", CancellationToken.None)).State);
        // and it can be started again
        await sup.StartAsync("crashy", false, 0, CancellationToken.None);
        await sup.StopAsync("crashy", false, TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    [Fact]
    public async Task Idle_timeout_ends_worker_and_removes_record()
    {
        using var home = new TestHome();
        using var sup = Sup(home);
        var st = await sup.StartAsync("idler", false, idleTimeoutSeconds: 2, CancellationToken.None);
        Assert.True(await TestPaths.WaitUntil(() => !TestPaths.ProcessAlive(st.Pid!.Value), TimeSpan.FromSeconds(20)));
        Assert.True(await TestPaths.WaitUntil(() => sup.KnownWorkerIds().Count == 0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Bound_worker_dies_with_its_supervisor_via_job_object()
    {
        using var home = new TestHome();
        var sup = Sup(home);
        var st = await sup.StartAsync("bound", boundToLauncher: true, 0, CancellationToken.None);
        Assert.True(TestPaths.ProcessAlive(st.Pid!.Value));
        sup.Dispose(); // closes the kill-on-close job handle, exactly like the host process exiting/crashing
        Assert.True(await TestPaths.WaitUntil(() => !TestPaths.ProcessAlive(st.Pid!.Value), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Shared_job_outlives_short_lived_supervisors_and_kills_on_host_exit()
    {
        // The studio creates a supervisor per request but owns one job for its lifetime.
        using var home = new TestHome();
        var job = new JobObject();
        int pid;
        using (var sup = new WorkerSupervisor(home.Storage(), TestPaths.WorkerExe, "studio", job))
            pid = (await sup.StartAsync("studio", boundToLauncher: true, 0, CancellationToken.None)).Pid!.Value;
        await Task.Delay(500);
        Assert.True(TestPaths.ProcessAlive(pid)); // disposing a per-request supervisor must not kill the engine
        job.Dispose();                            // the studio exiting (or crashing)
        Assert.True(await TestPaths.WaitUntil(() => !TestPaths.ProcessAlive(pid), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Force_stop_terminates_and_unload_recycles_to_a_new_process()
    {
        using var home = new TestHome();
        using var sup = Sup(home);
        var first = await sup.StartAsync("cycle", false, 0, CancellationToken.None);
        var recycled = await sup.RecycleAsync("cycle", CancellationToken.None);
        Assert.NotEqual(first.Pid, recycled.Pid);
        Assert.False(TestPaths.ProcessAlive(first.Pid!.Value));
        Assert.Equal("idle", recycled.State);

        var stop = await sup.StopAsync("cycle", force: true, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("forced", stop.Method);
        Assert.False(TestPaths.ProcessAlive(recycled.Pid!.Value));
    }

    [Fact]
    public async Task Cli_workers_start_does_not_hold_the_callers_stdout_pipe()
    {
        // Regression guard for handle inheritance: an agent capturing stdout must get EOF when the CLI exits,
        // even though the worker it launched keeps running.
        using var home = new TestHome();
        var psi = new ProcessStartInfo(TestPaths.CliExe, "workers start --worker piped --idle-timeout 60 --json --quiet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["NOORVOX_HOME"] = home.Root;
        using var cli = Process.Start(psi)!;
        var readAll = cli.StandardOutput.ReadToEndAsync();
        var finished = await Task.WhenAny(readAll, Task.Delay(TimeSpan.FromSeconds(30)));
        try
        {
            Assert.Same(readAll, finished);
            Assert.Contains("\"success\": true", await readAll);
        }
        finally
        {
            using var sup = Sup(home);
            await sup.StopAllAsync(force: true, TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }
}
