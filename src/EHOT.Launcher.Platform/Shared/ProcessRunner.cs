using System.Diagnostics;
using System.Text;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.Platform.Shared;

/// <summary>Кроссплатформенный запуск процессов (System.Diagnostics.Process работает на всех ОС).</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = CreateProcess(request, redirectOutput: true);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Не удалось запустить процесс {request.FileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), sw.Elapsed);
    }

    public IProcessHandle Start(ProcessRequest request)
    {
        var process = CreateProcess(request, redirectOutput: true);
        var handle = new ProcessHandle(process);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Не удалось запустить процесс {request.FileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return handle;
    }

    private static Process CreateProcess(ProcessRequest request, bool redirectOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrEmpty(request.WorkingDirectory))
        {
            psi.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }

    private sealed class ProcessHandle : IProcessHandle
    {
        private readonly Process _process;
        private int _exitCode = int.MinValue;

        public ProcessHandle(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    OutputReceived?.Invoke(new ProcessOutputLine(DateTimeOffset.Now, false, e.Data));
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    OutputReceived?.Invoke(new ProcessOutputLine(DateTimeOffset.Now, true, e.Data));
                }
            };
            _process.Exited += (_, _) =>
            {
                _exitCode = SafeExitCode();
                Exited?.Invoke(_exitCode);
            };
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _exitCode == int.MinValue ? SafeExitCode() : _exitCode;

        public event Action<ProcessOutputLine>? OutputReceived;

        public event Action<int>? Exited;

        public async Task<int> WaitForExitAsync(CancellationToken ct = default)
        {
            await _process.WaitForExitAsync(ct).ConfigureAwait(false);
            _exitCode = SafeExitCode();
            return _exitCode;
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private int SafeExitCode()
        {
            try
            {
                return _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
