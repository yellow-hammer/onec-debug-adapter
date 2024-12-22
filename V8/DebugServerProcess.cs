using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.Services;
using System.Diagnostics;

namespace Onec.DebugAdapter.V8
{
	public sealed class DebugServerProcess : IDisposable
	{
		private readonly IDebugConfiguration _configuration;
		private DebugProtocolClient _client = null!;
		private bool _needSendEvent = true;

		private Process? _process;
		private bool _disposedValue;

		public DebugServerProcess(IDebugConfiguration configuration)
		{
			_configuration = configuration;
		}

		public async Task Run(DebugProtocolClient client)
		{
			_client = client;


			var notifyFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

			var arguments = new[]
			{
				$"--addr={_configuration.DebugServerHost}",
				$"--portRange=1550:1559",
				$"--ownerPID={Environment.ProcessId}",
				$"--notify=\"{notifyFilePath}\""
			};

			var exePath = Path.Join(
				_configuration.PlatformBin, 
				Environment.OSVersion.Platform switch
				{
					PlatformID.Win32NT => "dbgs.exe",
					_ => "dbgs"
				});
			
			if (!File.Exists(exePath))
				throw new Exception("Исполняемый файл сервера отладки 1С не найден");

			_process = new Process
			{
				StartInfo = new ProcessStartInfo(exePath, string.Join(" ", arguments))
				{
					RedirectStandardError = true
				},
				EnableRaisingEvents = true,
			};
			_process.Exited += DebuggerExited;
			_process.Start();

			while (!_process.HasExited)
			{
				if (File.Exists(notifyFilePath))
				{
					try
					{
						await using var stream = File.Open(notifyFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
						using var reader = new StreamReader(stream);

						if (stream.Length <= 0) continue;
						var notifyData = await reader.ReadToEndAsync();
						_configuration.SetDebugServerPort(int.Parse(notifyData.Split(':')[1]));
						break;
					}
					catch (Exception)
					{
						// ignored
					}
				}
				else
					await Task.Delay(25);
			}

			if (File.Exists(notifyFilePath))
				File.Delete(notifyFilePath);
		}

		private void DebuggerExited(object? sender, EventArgs e)
		{
			if (!_needSendEvent) return;
			if (_process?.ExitCode != 0)
				_client.SendError(_process?.StandardError.ReadToEnd() ?? "");

			_client?.SendEvent(new TerminatedEvent());
		}

		private void Stop()
		{
			_needSendEvent = false;
			_process?.Kill();
		}

		private void Dispose(bool disposing)
		{
			if (_disposedValue) return;
			if (disposing)
			{
				// TODO: освободить управляемое состояние (управляемые объекты)
			}

			Stop();
			_disposedValue = true;
		}

		~DebugServerProcess()
		{
			Dispose(disposing: false);
		}

		public void Dispose()
		{
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}
}
