using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Netch.Enums;
using Netch.Models;
using Netch.Utils;
using Serilog;

namespace Netch.Controllers
{
    public abstract class Guard
    {
        private FileStream? _logFileStream;
        private StreamWriter? _logStreamWriter;

        /// <param name="mainFile">application path relative of Netch\bin</param>
        /// <param name="redirectOutput"></param>
        /// <param name="encoding">application output encode</param>
        protected Guard(string mainFile, bool redirectOutput = true, Encoding? encoding = null)
        {
            RedirectOutput = redirectOutput;

            var fileName = Path.GetFullPath($"bin\\{mainFile}");

            if (!File.Exists(fileName))
                throw new MessageException(i18N.Translate($"bin\\{mainFile} file not found!"));

            Instance = new Process
            {
                StartInfo =
                {
                    FileName = fileName,
                    WorkingDirectory = $"{Global.NetchDir}\\bin",
                    CreateNoWindow = true,
                    UseShellExecute = !RedirectOutput,
                    RedirectStandardOutput = RedirectOutput,
                    StandardOutputEncoding = RedirectOutput ? encoding : null,
                    RedirectStandardError = RedirectOutput,
                    StandardErrorEncoding = RedirectOutput ? encoding : null,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
        }

        protected string LogPath => Path.Combine(Global.NetchDir, $"logging\\{Name}.log");

        protected virtual IEnumerable<string> StartedKeywords { get; } = new List<string>();

        protected virtual IEnumerable<string> FailedKeywords { get; } = new List<string>();

        public abstract string Name { get; }

        private State State { get; set; } = State.Waiting;

        private bool RedirectOutput { get; }

        public Process Instance { get; }

        ~Guard()
        {
            _logFileStream?.Dispose();
            _logStreamWriter?.Dispose();
            Instance.Dispose();
        }

        protected async Task StartGuardAsync(string argument, ProcessPriorityClass priority = ProcessPriorityClass.Normal)
        {
            State = State.Starting;

            _logFileStream = File.Open(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _logStreamWriter = new StreamWriter(_logFileStream) { AutoFlush = true };

            Instance.StartInfo.Arguments = argument;
            Instance.Start();

            if (priority != ProcessPriorityClass.Normal)
                Instance.PriorityClass = priority;

            if (RedirectOutput)
            {
                Task.Run(() => ReadOutput(Instance.StandardOutput)).Forget();
                Task.Run(() => ReadOutput(Instance.StandardError)).Forget();

                if (!StartedKeywords.Any())
                {
                    // Skip, No started keyword
                    State = State.Started;
                    return;
                }

                // wait ReadOutput change State
                for (var i = 0; i < 1000; i++)
                {
                    await Task.Delay(50);
                    switch (State)
                    {
                        case State.Started:
                            OnStarted();
                            return;
                        case State.Stopped:
                            await StopGuardAsync();
                            OnStartFailed();
                            throw new MessageException($"{Name} 控制器启动失败");
                    }
                }

                await StopGuardAsync();
                throw new MessageException($"{Name} 控制器启动超时");
            }
        }

        private void ReadOutput(TextReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                _logStreamWriter!.WriteLine(line);
                OnReadNewLine(line);

                if (State == State.Starting)
                {
                    if (StartedKeywords.Any(s => line.Contains(s)))
                        State = State.Started;
                    else if (FailedKeywords.Any(s => line.Contains(s)))
                    {
                        OnStartFailed();
                        State = State.Stopped;
                    }
                }
            }

            State = State.Stopped;
        }

        public virtual async Task StopAsync()
        {
            await StopGuardAsync();
        }

        protected async Task StopGuardAsync()
        {
            _logStreamWriter?.Close();
            _logFileStream?.Close();

            try
            {
                if (Instance is { HasExited: false })
                {
                    Instance.Kill();
                    await Instance.WaitForExitAsync();
                }
            }
            catch (Win32Exception e)
            {
                Log.Error(e, "Stop {Name} failed", Instance.ProcessName);
            }
            catch
            {
                // ignored
            }
        }

        protected virtual void OnStarted()
        {
        }

        protected virtual void OnReadNewLine(string line)
        {
        }

        protected virtual void OnStartFailed()
        {
            Utils.Utils.Open(LogPath);
        }
    }
}