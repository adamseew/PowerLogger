// #define DEBUG

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO.Ports;
using System.Diagnostics;
using System.Threading.Tasks;
using Renci.SshNet;
using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;

namespace ConsoleApplication1
{
    class Program
    {
        private static DateTime previousSampleTime;
        private static StreamWriter powerLogFile;
        private static StreamWriter logFile;
        private static int serialCounter;

        public static void Main()
        {
            int sampleFrequency = 5;
            int counter = 0;
            int logSize;
            int usageLogSize;
            int powerLogSize;
            int iterate = 1;
            int beforePause = 10000;
            int afterPause = beforePause;

            string line;

            string host = "10.0.0.2";
            string userName = "ubuntu";
            string password = "ubuntu";
            string serialPortAddress = "COM3";

            string[] benchmarks = new string[] { "./NVIDIA_CUDA-6.5_Samples/bin/armv7l/linux/release/gnueabihf/scan"/*"cd darknet-2/ && ./image_yolov3.sh"*//*, "./shooting/build/shooting.x"*/};

            List<double> powerLog;
            StreamReader boardLog;

            // creating local log file (for power consumption)
            powerLogFile = new System.IO.StreamWriter("powerdata.log");

            // creating serial connection
            SerialPort serialPort = new SerialPort(serialPortAddress);

            serialPort.BaudRate = 115200;
            serialPort.Parity = Parity.None;
            serialPort.StopBits = StopBits.One;
            serialPort.DataBits = 8;
            serialPort.Handshake = Handshake.None;
            serialCounter = 0;
            serialPort.DataReceived += new SerialDataReceivedEventHandler(DataReceivedHandler);

            Console.WriteLine("connecting to serial port " + serialPortAddress);

            serialPort.Open();

            Console.WriteLine("connected (sync)");

            previousSampleTime = DateTime.Now;

            Console.WriteLine("connecting to board on " + host + " under user " + userName);

            //
            // board connection
            //

            Task.Factory.StartNew(() => {

                using (var client = new SshClient(host, userName, password)) {

                    // saving logs on tk1
                    client.Connect();

                    Console.WriteLine("connected (async)");

                    Console.WriteLine("started to store log data on board  at {0:mm.ss.ffffff}", DateTime.Now);

                    client.RunCommand(
                        "echo " + password + " | sudo -S ./tegrastats " +
                        ((int)(1000 * (1.0 / sampleFrequency))).ToString() +
                        " --logfile usagedata.log"
                    );

                    client.Disconnect();

                }
            });

            //
            // multimeter connection
            //

            Task.Factory.StartNew(() => {

                while (true) {

                    if (serialPort.IsOpen)
                        serialPort.WriteLine("VAL1?");
                    System.Threading.Thread.Sleep((int)(1000.0 / sampleFrequency));
                }

            });
            
            if (beforePause > 0)
                System.Threading.Thread.Sleep(afterPause);
            
            do {
                iterate--;

                counter = 0;
                Task[] tasks = new Task[benchmarks.Length];

                foreach (string benchmark in benchmarks) {

                    //
                    // running benchmarks
                    //

                    tasks[counter] = Task.Factory.StartNew(() => {

                        Console.WriteLine("running benchmark " + benchmark);

                        using (var client = new SshClient(host, userName, password)) {

                            // calling benchmark
                            client.Connect();
                            var output = client.RunCommand(benchmark);

                            // awaiting termination
                            Console.BackgroundColor = ConsoleColor.DarkGreen;
                            Console.Write(output.Result);
                            Console.BackgroundColor = ConsoleColor.Black;

                            Console.WriteLine("benchmark " + benchmark + " done");

                            client.Disconnect();
                        }
                    });

                    counter++;
                }

                Task.WaitAll(tasks);

            } while (iterate != 0) ;

            if (afterPause > 0) {

                // after pause for additional logging
                Console.WriteLine("logging another " + afterPause + " ms");

                System.Threading.Thread.Sleep(afterPause);
            }

            //
            // terminating / deleting
            //

            using (var client = new SshClient(host, userName, password)) {

                // stopping logs on tk1
                client.Connect();

                var output = client.RunCommand("echo " + password + " | sudo -S ./tegrastats --stop");

                Console.BackgroundColor = ConsoleColor.DarkGreen;
                Console.Write(output.Result);
                Console.BackgroundColor = ConsoleColor.Black;

                Console.WriteLine("log on board terminated at {0:mm.ss.ffffff}", DateTime.Now);

                client.Disconnect();
            }

            serialPort.Close();
            powerLogFile.Close();

            Console.WriteLine("log from multimiter terminated at {0:mm.ss.ffffff}", DateTime.Now);

            // downloading data from board
            using (ScpClient client = new ScpClient(host, userName, password)) {
                Console.WriteLine("retriving data from board");

                client.Connect();

                using (Stream localFile = File.Create("usagedata.log")) {
                   
                    client.Download("usagedata.log", localFile);
                }

                Console.WriteLine("retrived");
            }

            using (var client = new SshClient(host, userName, password)) {
                
                // deleting log on board
                client.Connect();
                var output1 = client.RunCommand("echo " + password + " | sudo chmod 777 usagedata.log");
                var output2 = client.RunCommand("rm usagedata.log");

                Console.BackgroundColor = ConsoleColor.DarkGreen;
                Console.Write(output1.Result);
                Console.Write(output2.Result);
                Console.BackgroundColor = ConsoleColor.Black;

                Console.WriteLine("log on board deleted");

                client.Disconnect();
            }
            
            //
            // processing logs
            //

            Console.WriteLine("processing data");

            usageLogSize = File.ReadLines(@"usagedata.log").Count();
            powerLogSize = File.ReadLines(@"powerdata.log").Count();

            logSize = 
                usageLogSize > powerLogSize ? powerLogSize : usageLogSize;

            // ??
            if (Math.Abs(File.ReadLines(@"usagedata.log").Count() -
                File.ReadLines(@"powerdata.log").Count()) > sampleFrequency * 8) {

                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.WriteLine("data from board and multimeter are unconsistent");
                Console.BackgroundColor = ConsoleColor.Black;
            }

            counter = 0;
            powerLog = new List<double>();
            boardLog = new StreamReader(@"powerdata.log");

            while ((line = boardLog.ReadLine()) != null) {
                
                counter++;
                
                // ??
                if (powerLogSize - usageLogSize - (counter) > 0)
                    continue;

                double power = double.Parse(line, CultureInfo.InvariantCulture);
                powerLog.Add(power);
            }

            counter = 0;
            boardLog = new StreamReader(@"usagedata.log");

            // creating local log file (for power consumption in A)
            logFile = new System.IO.StreamWriter("data.log");

            while ((line = boardLog.ReadLine()) != null && counter < powerLogSize) {

                string[] stringLine = line.Split(' ');

                string[] cpuUsage = stringLine[5].Split('@')[0].TrimStart('[').TrimEnd(']').Split(',');

                double cpuUsageCore1 = double.Parse(cpuUsage[0] == "off" ? "0" : cpuUsage[0].TrimEnd('%'));
                double cpuUsageCore2 = double.Parse(cpuUsage[1] == "off" ? "0" : cpuUsage[1].TrimEnd('%'));
                double cpuUsageCore3 = double.Parse(cpuUsage[2] == "off" ? "0" : cpuUsage[2].TrimEnd('%'));
                double cpuUsageCore4 = double.Parse(cpuUsage[3] == "off" ? "0" : cpuUsage[3].TrimEnd('%'));

                double gpuUsage = double.Parse(stringLine[13].Split('%')[0]);

                // time[relative] power[W*10] core1[%] core2 core3 core4 gpu[%] 
                logFile.WriteLine(String.Format(CultureInfo.InvariantCulture, 
                    "{0} {1:E} {2:E} {3:E} {4:E} {5:E} {6:E}",
                    counter,
                    powerLog.ElementAt(counter) * 12 * 10,
                    cpuUsageCore1, cpuUsageCore2, cpuUsageCore3, cpuUsageCore4, 
                    gpuUsage
                ));

                counter++;
            }

            logFile.Close();

            Console.WriteLine("data processed see data.log");

            Console.WriteLine("press any key to continue...");
            Console.WriteLine();
            Console.ReadKey();

        }

        private static void DataReceivedHandler(
                            object sender,
                            SerialDataReceivedEventArgs e)
        {
            SerialPort serialPort = (SerialPort)sender;

            if (serialCounter == 0)
                Console.WriteLine("started to sample data from multimeter at {0:mm.ss.ffffff}", DateTime.Now);

            serialCounter++;

            if (!serialPort.IsOpen)
                return;

            // Console.WriteLine("sampling frequency {0}", 1000.0 / 
            //     (DateTime.Now - previousSampleTime).Milliseconds);

            powerLogFile.Write(serialPort.ReadExisting());
            // previousSampleTime = DateTime.Now;
        }

    }
}
