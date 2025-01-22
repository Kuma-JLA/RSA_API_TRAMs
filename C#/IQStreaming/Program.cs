using System;
using System.Collections.Generic;
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

            // Default values
            int deviceId = 0;
            double centerFreq = 5220e6;
            double refLevel = -10;
            double span = 40e6;
            int msec = 10000;
            string filename = "iqstream";

            // Parse arguments
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

            // Search for devices.
            int[] devId = null;
            string[] devSn = null;
            string[] devType = null;
            var rs = api.DEVICE_Search(ref devId, ref devSn, ref devType);
            if (devId == null || deviceId >= devId.Length)
            {
                Console.WriteLine("\nNo devices found or invalid device ID!");
                return;
            }

            // Reset and connect to the selected device.
            if (rs == ReturnStatus.noError)
            {
                rs = api.DEVICE_Reset(devId[deviceId]);
                rs = api.DEVICE_Connect(devId[deviceId]);
            }

            if (rs != ReturnStatus.noError)
            {
                Console.WriteLine("\nERROR: " + rs);
                return;
            }
            else
            {
                Console.WriteLine("\nCONNECTED TO: " + devType[deviceId]);
            }

            // Set the center frequency and reference level.
            rs = api.CONFIG_SetCenterFreq(centerFreq);
            rs = api.CONFIG_SetReferenceLevel(refLevel);

            // Configure Auto Attenuation, RF Preamp, and RF Attenuator.
            rs = api.CONFIG_SetAutoAttenuationEnable(false); // Disable Auto Attenuation.
            rs = api.CONFIG_SetRFPreampEnable(true); // Enable RF Preamp.
            rs = api.CONFIG_SetRFAttenuator(0); // Set RF Attenuator to 0 dB.

            // Set the acquisition bandwidth before putting the device in Run mode.
            rs = api.IQSTREAM_SetAcqBandwidth(span);
            // Get the actual bandwidth and sample rate.
            double bwAct = 0;
            double srSps = 0;
            rs = api.IQSTREAM_GetAcqParameters(ref bwAct, ref srSps);

            Console.WriteLine("Bandwidth Requested: {0:F3} MHz, Actual: {1:F3} MHz", span / 1e6, bwAct / 1e6);
            Console.WriteLine("Sample Rate: {0:F} MS/s", srSps / 1e6);

            // Set the output configuration.
            var dest = IQSOUTDEST.IQSOD_FILE_TIQ; // Destination is a TIQ file in this example.
            var dtype = IQSOUTDTYPE.IQSODT_INT16; // Output type is a 16 bit integer.
            rs = api.IQSTREAM_SetOutputConfiguration(dest, dtype);

            // Register the settings for the output file.
            var fnsuffix = IQSSDFN_SUFFIX.IQSSDFN_SUFFIX_NONE;
            rs = api.IQSTREAM_SetDiskFileLength(msec);
            rs = api.IQSTREAM_SetDiskFilenameBase(filename);
            rs = api.IQSTREAM_SetDiskFilenameSuffix(fnsuffix);

            // Start the live IQ capture.
            var numSamples = 0UL;
            var isActive = true;
            var iqInfo = new IQSTRMIQINFO();
            var fileinfo = new IQSTRMFILEINFO();
            // Put the device into Run mode before starting IQ capture.
            rs = api.DEVICE_Run();
            Console.WriteLine("\nIQ Capture starting...");
            rs = api.IQSTREAM_Start();
            while (isActive)
            {
                // Determine if the write is complete.
                var complete = false;
                var writing = false;
                rs = api.IQSTREAM_GetDiskFileWriteStatus(ref complete, ref writing);
                isActive = !complete;
                rs = api.IQSTREAM_GetDiskFileInfo(ref fileinfo);
                numSamples = fileinfo.numberSamples;
            }

            Console.WriteLine("{0} Samples written to tiq file.", numSamples);

            // Disconnect the device and finish up.
            rs = api.IQSTREAM_Stop();
            rs = api.DEVICE_Stop();
            rs = api.DEVICE_Disconnect();

            Console.WriteLine("\nIQ streaming routine complete.");
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
