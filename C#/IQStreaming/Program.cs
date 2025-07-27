using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Tektronix;

namespace IQStreaming
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            if (args.Contains("?"))
            {
                PrintUsage();
                return;
            }

            //デフォルト設定
            int deviceId = 0;
            double centerFreq = 5220e6;
            double refLevel = -25;
            double span = 40e6;
            int msec = 10000;
            string filename = "iqstream";

            // ==== 引数パース ====
            foreach (var arg in args)
            {
                if (arg.StartsWith("dev=")) deviceId = int.Parse(arg.Split('=')[1]);
                else if (arg.StartsWith("cf=")) centerFreq = double.Parse(arg.Split('=')[1]);
                else if (arg.StartsWith("rl=")) refLevel = double.Parse(arg.Split('=')[1]);
                else if (arg.StartsWith("bw=")) span = double.Parse(arg.Split('=')[1]);
                else if (arg.StartsWith("msec=")) msec = int.Parse(arg.Split('=')[1]);
                else if (arg.StartsWith("fn=")) filename = arg.Split('=')[1];
            }

            var api = new APIWrapper();
            ReturnStatus rs;

            //デバイス検索・接続
            int[] devId = null;
            string[] devSn = null;
            string[] devType = null;
            rs = api.DEVICE_Search(ref devId, ref devSn, ref devType);
            if (devId == null || deviceId >= devId.Length)
            {
                Console.WriteLine("ERROR: No devices found or invalid device ID!");
                return;
            }

            rs = api.DEVICE_Reset(devId[deviceId]);
            rs = api.DEVICE_Connect(devId[deviceId]);
            if (rs != ReturnStatus.noError)
            {
                Console.WriteLine("ERROR: " + rs);
                return;
            }

            Console.WriteLine($"CONNECTED TO: {devType[deviceId]}");

            //コンフィグ設定
            api.CONFIG_SetCenterFreq(centerFreq);
            api.CONFIG_SetReferenceLevel(refLevel);
            api.CONFIG_SetAutoAttenuationEnable(false);
            api.CONFIG_SetRFPreampEnable(true);
            api.CONFIG_SetRFAttenuator(0);

            //帯域幅設定
            api.IQSTREAM_SetAcqBandwidth(span);
            double bwAct = 0, srSps = 0;
            api.IQSTREAM_GetAcqParameters(ref bwAct, ref srSps);
            Console.WriteLine($"Bandwidth Requested: {span / 1e6:F3} MHz, Actual: {bwAct / 1e6:F3} MHz");
            Console.WriteLine($"Sample Rate: {srSps / 1e6:F} MS/s");

            //出力設定
            api.IQSTREAM_SetOutputConfiguration(IQSOUTDEST.IQSOD_FILE_TIQ, IQSOUTDTYPE.IQSODT_INT16);
            api.IQSTREAM_SetDiskFileLength(msec);
            api.IQSTREAM_SetDiskFilenameBase(filename);
            api.IQSTREAM_SetDiskFilenameSuffix(IQSSDFN_SUFFIX.IQSSDFN_SUFFIX_NONE);

            //測定＋進捗表示＋再試行
            double totalSamples = srSps * msec / 1000.0;
            int retryCount = 0;

            Console.WriteLine("\n測定開始...");
            api.DEVICE_Run();
            api.IQSTREAM_Start();
            Console.WriteLine("測定中...");

            ulong previousSamples = 0;
            var stagnationTimer = Stopwatch.StartNew();
            ulong numSamples = 0;

            while (true)
            {
                bool complete = false, writing = false;
                api.IQSTREAM_GetDiskFileWriteStatus(ref complete, ref writing);

                var fileinfo = new IQSTRMFILEINFO();
                api.IQSTREAM_GetDiskFileInfo(ref fileinfo);
                numSamples = fileinfo.numberSamples;

                //進捗更新
                double progress = numSamples / totalSamples * 100.0;
                Console.Write($"\rProgress: {progress:F1}% ({numSamples} samples)");

                //進捗変化チェック
                if (numSamples != previousSamples)
                {
                    previousSamples = numSamples;
                    stagnationTimer.Restart();
                }
                else if (stagnationTimer.Elapsed.TotalSeconds >= 10)
                {
                    if (retryCount < 1)
                    {
                        Console.WriteLine("\n10秒間進捗なし検出。再試行します。");
                        api.IQSTREAM_Stop();
                        api.IQSTREAM_Start();
                        retryCount++;
                        previousSamples = 0;
                        stagnationTimer.Restart();
                        continue;
                    }
                    else
                    {
                        Console.WriteLine("\n再試行回数上限に達したため測定を終了します。");
                        break;
                    }
                }

                if (complete)
                    break;
            }

            Console.WriteLine();  //進捗バーの末尾改行
            Console.WriteLine($"\n{numSamples} Samples written to tiq file.");

            //後片付け
            api.IQSTREAM_Stop();
            api.DEVICE_Stop();
            api.DEVICE_Disconnect();

            Console.WriteLine("測定完了");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("--- IQStreaming Application ---");
            Console.WriteLine("Usage: IQStreaming [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  dev=<devid>      Device ID of device to connect (default: 0)");
            Console.WriteLine("  cf=<ctrFreqHz>   RF Center Frequency in Hz (default: 5220e6)");
            Console.WriteLine("  rl=<refLeveldBm> RF Input Reference Level in dBm (default: -10)");
            Console.WriteLine("  bw=<reqBW>       Requested IQ Bandwidth in Hz (default: 40e6)");
            Console.WriteLine("  msec=<outlen>    Length of Output in milliseconds (default: 10000)");
            Console.WriteLine("  fn=<filename>    Output Filename Base (default: 'iqstream')");
            Console.WriteLine("Examples:");
            Console.WriteLine("  IQStreaming dev=0 cf=2.4e9 rl=-20 bw=20e6 msec=5000 fn=mycapture");
        }
    }
}
