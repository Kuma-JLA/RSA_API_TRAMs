// File: DPXCsvExporter.cs
// Build: csc DPXCsvExporter.cs (または Visual Studio / dotnet でプロジェクト化)
// 必須: Tektronix API ライブラリ（元サンプルの APIWrapper 等）への参照を設定してください。

using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using Tektronix;

namespace DPXCsvExporter
{
    class Program
    {
        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  DPXCsvExporter.exe [--device <deviceID>] [--center <Hz>] [--bandwidth <Hz>]");
            Console.WriteLine("                     [--rbw <Hz>] [--tracelength <points>] [--frames <count>] [--reflevel <dBm>]");
            Console.WriteLine("Examples:");
            Console.WriteLine("  DPXCsvExporter.exe --device 0 --center 103300000 --bandwidth 40000000 --rbw 5000000 --tracelength 1024 --frames 100 --reflevel -10");
        }

        static double[] Linspace(double start, double stop, int num)
        {
            if (num == 1) return new double[] { start };
            double[] r = new double[num];
            double step = (stop - start) / (num - 1);
            for (int i = 0; i < num; i++) r[i] = start + i * step;
            return r;
        }

        static float[] ResampleLinear(float[] src, int targetLen)
        {
            if (src == null) return new float[targetLen];
            if (src.Length == targetLen) return (float[])src.Clone();
            float[] outArr = new float[targetLen];
            int n = src.Length;
            for (int i = 0; i < targetLen; i++)
            {
                double pos = (double)i * (n - 1) / (targetLen - 1);
                int i0 = (int)Math.Floor(pos);
                int i1 = Math.Min(i0 + 1, n - 1);
                double frac = pos - i0;
                outArr[i] = (float)((1.0 - frac) * src[i0] + frac * src[i1]);
            }
            return outArr;
        }

        // 変換ルールは必ずAPIドキュメントで確認してください。
        // ここでは p (W) -> dBm: 10*log10(p * 1e3) を仮定しています。
        static double[] ConvertToDbm(float[] linearPowerArray)
        {
            var outArr = new double[linearPowerArray.Length];
            for (int i = 0; i < linearPowerArray.Length; i++)
            {
                double val = linearPowerArray[i];
                if (val <= 0)
                {
                    // 非正値は非常に小さい値に置き換える（-inf を避ける）
                    outArr[i] = -300.0;
                }
                else
                {
                    outArr[i] = 10.0 * Math.Log10(val * 1e3); // W -> mW -> dBm
                }
            }
            return outArr;
        }

        static void Main(string[] args)
        {
            // --- デフォルト値 ---
            int? requestedDeviceId = null;
            double centerFreq = 103.3e6;
            double bandwidth = 40e6;
            double RBW = 5e6;
            int requestedTraceLength = 0; // 0 = use device's trace length
            int numFrames = 100;
            double refLevel = -10.0;
            int traceIndexToUse = 0; // use first spectrum trace by default

            // --- 引数解析（非常にシンプル）---
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].ToLowerInvariant();
                try
                {
                    if (a == "--device" && i + 1 < args.Length) { requestedDeviceId = int.Parse(args[++i]); }
                    else if (a == "--center" && i + 1 < args.Length) { centerFreq = double.Parse(args[++i], CultureInfo.InvariantCulture); }
                    else if (a == "--bandwidth" && i + 1 < args.Length) { bandwidth = double.Parse(args[++i], CultureInfo.InvariantCulture); }
                    else if (a == "--rbw" && i + 1 < args.Length) { RBW = double.Parse(args[++i], CultureInfo.InvariantCulture); }
                    else if (a == "--tracelength" && i + 1 < args.Length) { requestedTraceLength = int.Parse(args[++i]); }
                    else if (a == "--frames" && i + 1 < args.Length) { numFrames = int.Parse(args[++i]); }
                    else if (a == "--reflevel" && i + 1 < args.Length) { refLevel = double.Parse(args[++i], CultureInfo.InvariantCulture); }
                    else if (a == "--traceindex" && i + 1 < args.Length) { traceIndexToUse = int.Parse(args[++i]); }
                    else { /* ignore unknown */ }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Argument parse error: " + ex.Message);
                    PrintUsage();
                    return;
                }
            }

            APIWrapper api = new APIWrapper();

            // --- デバイス検索 ---
            int[] devID = null;
            string[] devSN = null;
            string[] devType = null;
            ReturnStatus rs = api.DEVICE_Search(ref devID, ref devSN, ref devType);
            if (rs != ReturnStatus.noError || devID == null || devID.Length == 0)
            {
                Console.WriteLine("No devices found or DEVICE_Search failed: {0}", rs);
                return;
            }

            int useDeviceId = requestedDeviceId ?? devID[0];
            if (!devID.Contains(useDeviceId))
            {
                Console.WriteLine("Requested device id {0} not found. Available: {1}", useDeviceId, string.Join(",", devID));
                return;
            }

            // --- 接続 ---
            rs = api.DEVICE_Reset(useDeviceId);
            rs = api.DEVICE_Connect(useDeviceId);
            if (rs != ReturnStatus.noError)
            {
                Console.WriteLine("ERROR connecting to device {0}: {1}", useDeviceId, rs);
                return;
            }
            Console.WriteLine("Connected to device {0}", useDeviceId);

            // --- 基本設定 ---
            rs = api.CONFIG_SetCenterFreq(centerFreq);
            rs = api.CONFIG_SetReferenceLevel(refLevel);

            // RBW 範囲照会（元サンプル同様）
            double minRBW = 0, maxRBW = 0;
            rs = api.DPX_GetRBWRange(bandwidth, ref minRBW, ref maxRBW);
            Console.WriteLine("Bandwidth request: {0} Hz, RBW range: min {1} Hz, max {2} Hz", bandwidth, minRBW, maxRBW);

            // DPX 初期化とパラメータ設定
            rs = api.DPX_Reset();
            int width = 200; // この幅は DPX bitmap width の設定（多くの機器は自動決定）
            int tracepts = 1;
            VerticalUnitType vertunits = VerticalUnitType.VerticalUnit_dBm;
            double yTop = 0.0, yBottom = -100.0;
            bool infPersist = false, showOnlyTrigFrame = false;
            double persistTime = 1.0;
            rs = api.DPX_SetParameters(bandwidth, RBW, width, tracepts, vertunits, yTop, yBottom, infPersist, persistTime, showOnlyTrigFrame);
            rs = api.DPX_Configure(true, false); // スペクトラム ON, スペクトログラム OFF

            // トレースタイプ等（元サンプルと同様）
            rs = api.DPX_SetSpectrumTraceType(0, TraceType.TraceTypeMax);
            rs = api.DPX_SetSpectrumTraceType(1, TraceType.TraceTypeMin);
            rs = api.DPX_SetSpectrumTraceType(2, TraceType.TraceTypeAverage);

            // 設定取得
            DPX_SettingsStruct getSettings = new DPX_SettingsStruct();
            rs = api.DPX_GetSettings(ref getSettings);
            Console.WriteLine("DPX settings: bitmapWidth={0}, bitmapHeight={1}, traceLength(device)={2}, actualRBW={3} MHz",
                getSettings.bitmapWidth, getSettings.bitmapHeight, getSettings.traceLength, getSettings.actualRBW / 1e6);

            // フレームバッファ領域の確保
            var frameBuffer = new DPX_FrameBuffer();

            // DPX 有効化 -> Run
            rs = api.DPX_SetEnable(true);
            rs = api.DEVICE_Run();

            // CSV 出力準備
            string csvFileName = string.Format("DPX_{0}_{1}Hz_{2}BW_{3}frames.csv", useDeviceId, (long)centerFreq, (long)bandwidth, numFrames);
            using (var csv = new StreamWriter(csvFileName, false, System.Text.Encoding.UTF8))
            {
                csv.NewLine = "\n";
                Console.WriteLine("CSV output: " + csvFileName);

                // 実取得トレース長（機器から）
                int actualDeviceTraceLength = getSettings.traceLength > 0 ? getSettings.traceLength : 0;
                int targetTraceLen = actualDeviceTraceLength;
                if (requestedTraceLength > 0) targetTraceLen = requestedTraceLength;
                if (targetTraceLen <= 0)
                {
                    // フェールセーフ
                    targetTraceLen = 1024;
                }

                // frequency array (Hz) --- center +/- bandwidth/2 を targetTraceLen 点で分割
                double startFreq = centerFreq - (bandwidth / 2.0);
                double stopFreq = centerFreq + (bandwidth / 2.0);
                double[] freqs = Linspace(startFreq, stopFreq, targetTraceLen);

                // CSV ヘッダ行を書き出し
                // header: timestamp(yyyy-mm-dd hh:mm:ss.ffffff), freq1[Hz], freq2[Hz], ...
                var header = new List<string>();
                header.Add("timestamp(yyyy-MM-dd HH:mm:ss.ffffff)");
                foreach (var f in freqs)
                {
                    header.Add(((long)f).ToString(CultureInfo.InvariantCulture));
                }
                csv.WriteLine(string.Join(",", header));

                // --- 取得ループ ---
                int framesWritten = 0;
                int waitTimeoutMsec = 1000;
                int numTimeouts = 3;
                int timeoutCount = 0;
                long frameAvailCount = 0;

                while (framesWritten < numFrames && timeoutCount < numTimeouts)
                {
                    bool isDpxReady = false;
                    rs = api.DPX_WaitForDataReady(waitTimeoutMsec, ref isDpxReady);
                    if (rs != ReturnStatus.noError)
                    {
                        Console.WriteLine("DPX_WaitForDataReady returned {0}", rs);
                        timeoutCount++;
                        continue;
                    }

                    bool frameAvail = false;
                    if (isDpxReady)
                    {
                        rs = api.DPX_IsFrameBufferAvailable(ref frameAvail);
                        if (rs != ReturnStatus.noError)
                        {
                            Console.WriteLine("DPX_IsFrameBufferAvailable returned {0}", rs);
                            timeoutCount++;
                            continue;
                        }
                    }
                    else
                    {
                        timeoutCount++;
                        continue;
                    }

                    if (frameAvail)
                    {
                        frameAvailCount++;
                        rs = api.DPX_GetFrameBuffer(ref frameBuffer);
                        if (rs != ReturnStatus.noError)
                        {
                            Console.WriteLine("DPX_GetFrameBuffer returned {0}", rs);
                            break;
                        }

                        api.DPX_GetFrameInfo(ref frameAvailCount, ref frameAvailCount); // 元サンプルと同様に info を取得（不要なら削除可）

                        int deviceTraceLen = frameBuffer.spectrumTraceLength;
                        int numTraces = frameBuffer.numSpectrumTraces;
                        if (traceIndexToUse >= numTraces)
                        {
                            Console.WriteLine("Requested traceIndex {0} >= numTraces {1}. Using trace 0.", traceIndexToUse, numTraces);
                            traceIndexToUse = 0;
                        }

                        float[] selectedTrace = frameBuffer.spectrumTraces[traceIndexToUse];
                        // Resample to requested trace length if needed
                        float[] traceForCsv;
                        if (requestedTraceLength > 0 && requestedTraceLength != deviceTraceLen)
                        {
                            traceForCsv = ResampleLinear(selectedTrace, targetTraceLen);
                        }
                        else
                        {
                            // deviceTraceLen may differ from targetTraceLen when requestedTraceLength == 0 but getSettings.traceLength was nonzero
                            if (deviceTraceLen != targetTraceLen)
                            {
                                // adjust freqs to deviceTraceLen to match actual data
                                if (requestedTraceLength == 0)
                                {
                                    targetTraceLen = deviceTraceLen;
                                    freqs = Linspace(centerFreq - bandwidth / 2.0, centerFreq + bandwidth / 2.0, targetTraceLen);
                                }
                            }
                            traceForCsv = (deviceTraceLen == targetTraceLen) ? selectedTrace : ResampleLinear(selectedTrace, targetTraceLen);
                        }

                        double[] dbms = ConvertToDbm(traceForCsv);

                        // timestamp
                        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

                        // CSV 行を書き込み (timestamp, val1, val2, ...)
                        var line = new List<string> { ts };
                        for (int i = 0; i < dbms.Length; i++)
                        {
                            // 出力の精度は必要に応じて調整してください
                            line.Add(dbms[i].ToString("F6", CultureInfo.InvariantCulture));
                        }
                        csv.WriteLine(string.Join(",", line));
                        csv.Flush();

                        framesWritten++;

                        // 必要ならビットマップ等も同様に保存可能（ここでは要求に沿いトレースのみCSV）
                        api.DPX_FinishFrameBuffer();
                    }
                } // while

                Console.WriteLine("Frames written: {0}", framesWritten);
            } // using csv

            // --- 後片付け ---
            rs = api.DPX_SetEnable(false);
            rs = api.DEVICE_Stop();
            rs = api.DEVICE_Disconnect();

            Console.WriteLine("Finished. CSV saved.");
        }
    }
}
