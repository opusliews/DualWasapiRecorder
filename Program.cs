using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;

enum CaptureMode
{
	Input,
	Loopback
}

static class Program
{
	static void TryFinalize(string PartPath, string FinalPath)
	{
		try
		{
			if (File.Exists(PartPath))
			{
				if (File.Exists(FinalPath))
					File.Delete(FinalPath);

				File.Move(PartPath, FinalPath);
			}
		}
		catch (Exception Ex)
		{
			Console.WriteLine("Finalize rename failed: " + Ex.Message);
		}
	}

	
	static int Main(string[] Args)
	{
		try
		{
			var Options = ParseArgs(Args);
			if (Options.ShowDevices)
			{
				PrintDevices();
				return 0;
			}

			if (string.IsNullOrWhiteSpace(Options.DeviceAQuery) || string.IsNullOrWhiteSpace(Options.DeviceBQuery))
			{
				Console.WriteLine("Missing --a and/or --b. Use --list to see devices.");
				return 2;
			}

			Directory.CreateDirectory(Options.OutDir);

			var Enumerator = new MMDeviceEnumerator();

			// A is always treated as an INPUT capture device (e.g. VB-Cable Output)
			var DeviceA = FindDevice(Enumerator, DataFlow.Capture, Options.DeviceAQuery);
			if (DeviceA == null)
				throw new Exception($"Could not find INPUT device matching: \"{Options.DeviceAQuery}\". Try --list.");

			MMDevice? DeviceB = null;

			if (Options.DeviceBMode == CaptureMode.Input)
			{
				DeviceB = FindDevice(Enumerator, DataFlow.Capture, Options.DeviceBQuery);
				if (DeviceB == null)
					throw new Exception($"Could not find INPUT device matching: \"{Options.DeviceBQuery}\". Try --list.");
			}
			else
			{
				DeviceB = FindDevice(Enumerator, DataFlow.Render, Options.DeviceBQuery);
				if (DeviceB == null)
					throw new Exception($"Could not find OUTPUT device matching: \"{Options.DeviceBQuery}\". Try --list.");
			}

			var Stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			var FinalFileA = Path.Combine(Options.OutDir, $"A_{SafeName(DeviceA.FriendlyName)}_{Stamp}.mp3");
			var FinalFileB = Path.Combine(Options.OutDir, $"B_{SafeName(DeviceB.FriendlyName)}_{Stamp}.mp3");

			var FileA = FinalFileA + ".part";
			var FileB = FinalFileB + ".part";


			Console.WriteLine("Device A (input):  " + DeviceA.FriendlyName);
			Console.WriteLine("Device B (" + Options.DeviceBMode.ToString().ToLower() + "): " + DeviceB.FriendlyName);
			Console.WriteLine("Output A: " + FinalFileA);
			Console.WriteLine("Output B: " + FinalFileB);
			Console.WriteLine();
			Console.WriteLine("Recording... Press ENTER to stop.");
			Console.WriteLine("Tip: run with --list to see exact device names.");
			Console.WriteLine();

			using var CaptureA = new WasapiCapture(DeviceA);
			CaptureA.ShareMode = AudioClientShareMode.Shared;

			// For B, choose Input capture or Loopback capture
			WasapiCapture? CaptureBInput = null;
			WasapiLoopbackCapture? CaptureBLoop = null;

			LameMP3FileWriter? WriterA = null;
			LameMP3FileWriter? WriterB = null;
			
			var WriterLockA = new object();
			var WriterLockB = new object();
			
			long BytesA = 0;
			long BytesB = 0;

			var StartTime = DateTime.UtcNow;

			CaptureA.DataAvailable += (Sender, E) =>
			{
				lock (WriterLockA)
				{
					WriterA ??= new LameMP3FileWriter(FileA, CaptureA.WaveFormat, 192);
					WriterA.Write(E.Buffer, 0, E.BytesRecorded);
					BytesA += E.BytesRecorded;
				}
			};

			CaptureA.RecordingStopped += (Sender, E) =>
			{
				if (E.Exception != null) Console.WriteLine("A stopped with error: " + E.Exception.Message);
			};

			if (Options.DeviceBMode == CaptureMode.Input)
			{
				CaptureBInput = new WasapiCapture(DeviceB);
				CaptureBInput.ShareMode = AudioClientShareMode.Shared;

				CaptureBInput.DataAvailable += (Sender, E) =>
				{
					lock (WriterLockB)
					{
						WriterB ??= new LameMP3FileWriter(FileB, CaptureBInput.WaveFormat, 192);
						WriterB.Write(E.Buffer, 0, E.BytesRecorded);
						BytesB += E.BytesRecorded;
					}
				};

				CaptureBInput.RecordingStopped += (Sender, E) =>
				{
					if (E.Exception != null) Console.WriteLine("B stopped with error: " + E.Exception.Message);
				};
			}
			else
			{
				CaptureBLoop = new WasapiLoopbackCapture(DeviceB);

				CaptureBLoop.DataAvailable += (Sender, E) =>
				{
					lock (WriterLockB)
					{
						WriterB ??= new LameMP3FileWriter(FileB, CaptureBLoop.WaveFormat, 192);
						WriterB.Write(E.Buffer, 0, E.BytesRecorded);
						BytesB += E.BytesRecorded;
					}
				};

				CaptureBLoop.RecordingStopped += (Sender, E) =>
				{
					if (E.Exception != null) Console.WriteLine("B stopped with error: " + E.Exception.Message);
				};
			}

			using var Heartbeat = new System.Timers.Timer(10000);
			Heartbeat.AutoReset = true;
			Heartbeat.Elapsed += (s, e) =>
			{
				var Elapsed = DateTime.UtcNow - StartTime;
				var Sec = (int)Elapsed.TotalSeconds;

				// KB written (input bytes). MP3 files will be smaller, but this shows activity reliably.
				var KBA = BytesA / 1024;
				var KBB = BytesB / 1024;

				Console.WriteLine($"[{Sec}s] A: {KBA} KB in, B: {KBB} KB in");
			};
			Heartbeat.Start();

			
			CaptureA.StartRecording();
			if (CaptureBInput != null) CaptureBInput.StartRecording();
			if (CaptureBLoop != null) CaptureBLoop.StartRecording();

			Console.ReadLine();

			CaptureA.StopRecording();
			if (CaptureBInput != null) CaptureBInput.StopRecording();
			if (CaptureBLoop != null) CaptureBLoop.StopRecording();

			lock (WriterLockA) { WriterA?.Dispose(); WriterA = null; }
			lock (WriterLockB) { WriterB?.Dispose(); WriterB = null; }
			
			TryFinalize(FileA, FinalFileA);
			TryFinalize(FileB, FinalFileB);
			
			Heartbeat.Stop();
			
			Console.WriteLine("Stopped.");
			return 0;
		}
		catch (Exception Ex)
		{
			Console.WriteLine("ERROR: " + Ex.Message);
			Console.WriteLine("Try: dotnet run -- --list");
			return 1;
		}
	}

	static void PrintDevices()
	{
		var Enumerator = new MMDeviceEnumerator();

		Console.WriteLine("=== INPUT (Capture) devices ===");
		foreach (var D in Enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
			Console.WriteLine(" - " + D.FriendlyName);

		Console.WriteLine();
		Console.WriteLine("=== OUTPUT (Render) devices ===");
		foreach (var D in Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
			Console.WriteLine(" - " + D.FriendlyName);

		Console.WriteLine();
		Console.WriteLine("Use --a <substring> and --b <substring> to select devices.");
	}

	static MMDevice? FindDevice(MMDeviceEnumerator Enumerator, DataFlow Flow, string Query)
	{
		var Devices = Enumerator.EnumerateAudioEndPoints(Flow, DeviceState.Active).ToList();
		return Devices.FirstOrDefault(D => D.FriendlyName.Contains(Query, StringComparison.OrdinalIgnoreCase));
	}

	static string SafeName(string Name)
	{
		var Invalid = Path.GetInvalidFileNameChars();
		return new string(Name.Select(C => Invalid.Contains(C) ? '_' : C).ToArray());
	}

	sealed class Options
	{
		public bool ShowDevices { get; set; }
		public string DeviceAQuery { get; set; } = "";
		public string DeviceBQuery { get; set; } = "";
		public CaptureMode DeviceBMode { get; set; } = CaptureMode.Loopback;
		public string OutDir { get; set; } = ".";
	}

	static Options ParseArgs(string[] Args)
	{
		var O = new Options();
		for (int I = 0; I < Args.Length; I++)
		{
			var A = Args[I];

			if (A == "--list")
			{
				O.ShowDevices = true;
			}
			else if (A == "--a" && I + 1 < Args.Length)
			{
				O.DeviceAQuery = Args[++I];
			}
			else if (A == "--b" && I + 1 < Args.Length)
			{
				O.DeviceBQuery = Args[++I];
			}
			else if (A == "--bmode" && I + 1 < Args.Length)
			{
				var M = Args[++I].Trim().ToLowerInvariant();
				O.DeviceBMode = (M == "input") ? CaptureMode.Input : CaptureMode.Loopback;
			}
			else if (A == "--outdir" && I + 1 < Args.Length)
			{
				O.OutDir = Args[++I];
			}
		}
		return O;
	}
}
