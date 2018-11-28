using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Authentication;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;

namespace Herd
{


    public class HerdAgentInfo
    {
        private IPEndPoint m_ipAddress;
        public IPEndPoint ipAddress { get { return m_ipAddress; } set { m_ipAddress = value; } }

        public string ipAddressString
        {
            get
            {
                return m_ipAddress.Address.ToString();
            }
        }

        public DateTime lastACK { get { return m_lastACK; } set { m_lastACK = value; } }
        private DateTime m_lastACK;
        private Dictionary<string, string> m_properties;

        public HerdAgentInfo()
        {
            m_properties = new Dictionary<string, string>();
        }

        /// <summary>
        /// Constructor used for testing purposes
        /// </summary>
        public HerdAgentInfo(string processorId, int numCPUCores, string architecture, string CUDAVersion, string herdAgentVersion)
        {
            m_ipAddress = new IPEndPoint(0, 0);
            m_properties = new Dictionary<string, string>
            {
                [PropNames.ProcessorId] = processorId,
                [PropNames.NumCPUCores] = numCPUCores.ToString(),
                [PropNames.Architecture] = architecture,
                [PropNames.CUDA] = CUDAVersion,
                [PropNames.HerdAgentVersion] = herdAgentVersion
            };
        }

        public void AddProperty(string name, string value)
        {
            m_properties.Add(name, value);
        }

        public string Property(string name)
        {
            if (m_properties.ContainsKey(name))
                return m_properties[name];

            return PropValues.None;
        }


        public void Parse(XElement xmlDescription)
        {
            if (xmlDescription.Name.ToString() == XmlTags.HerdAgentDescription)
            {
                m_properties.Clear();

                foreach (XElement child in xmlDescription.Elements())
                    AddProperty(child.Name.ToString(), child.Value);
            }
        }


        public override string ToString()
        {
            string res = "";
            foreach (var property in m_properties)
                res += property.Key + "=\"" + property.Value + "\";";

            return res;
        }


        public string State { get { return Property(PropNames.State); } set { } }

        public string Version { get { return Property(PropNames.HerdAgentVersion); } set { } }

        public string ProcessorId { get { return Property(PropNames.ProcessorId); } }

        public int NumProcessors
        {
            get
            {
                string prop = Property(PropNames.NumCPUCores);
                if (prop == PropValues.None)
                    prop = Property(Deprecated.NumCPUCores); //for retrocompatibility
                return (!prop.Equals(PropValues.None)) ? int.Parse(prop) : 0;
            }
        }

        public string ProcessorArchitecture
        {
            get
            {
                string prop = Property(PropNames.Architecture);
                if (prop == PropValues.None)
                {
                    prop = Property(Deprecated.ProcessorArchitecture); //for retrocompatibility
                    if (prop == "AMD64" || prop == "IA64")
                        return PropValues.Win64;
                    else
                        return PropValues.Win32;
                }
                else
                    return prop;
            }
        }

        public double ProcessorLoad
        {
            get
            {
                string prop = Property(PropNames.ProcessorLoad);
                return (!prop.Equals(PropValues.None)) ? double.Parse(prop) : 0.0;
            }
        }

        public double Memory
        {
            get
            {
                string prop = Property(PropNames.TotalMemory);
                return (!prop.Equals(PropValues.None)) ? double.Parse(prop) : 0.0;
            }
        }

        public string CUDA
        {
            get
            {
                string prop = Property(PropNames.CUDA);
                if (prop == PropValues.None)
                    prop = Property(Deprecated.CUDA);  //for retrocompatibility
                return prop;
            }
        }

        public bool IsAvailable
        {
            get { if (Property(PropNames.State) == PropValues.StateAvailable) return true; return false; }
            set { }
        }
    }


    public class HerdAgentUdpState
    {
        //    public HerdAgent herdAgent { get; set; }
        public UdpClient client { get; set; }
        public IPEndPoint ip { get; set; }
    }


    public class HerdAgentTcpState
    {
        //    public HerdAgent herdAgent { get; set; }
        public IPEndPoint ip { get; set; }
    }


    public class HerdAgent : JobDispatcher
    {
        //[DllImport("kernel32.dll")]
        //private static extern void GetNativeSystemInfo(ref SYSTEM_INFO lpSystemInfo);

        private const int PROCESSOR_ARCHITECTURE_AMD64 = 9;
        private const int PROCESSOR_ARCHITECTURE_IA64 = 6;
        private const int PROCESSOR_ARCHITECTURE_INTEL = 0;
        /*
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_INFO
        {
            public short wProcessorArchitecture;
            public short wReserved;
            public int dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public int dwNumberOfProcessors;
            public int dwProcessorType;
            public int dwAllocationGranularity;
            public short wProcessorLevel;
            public short wProcessorRevision;
        }*/
        public const string FirewallExceptionNameTCP = "HerdAgentFirewallExceptionTCP";
        public const string FirewallExceptionNameUDP = "HerdAgentFirewallExceptionUDP";
        private object m_quitExecutionLock = new object();
        public const int m_remotelyCancelledErrorCode = -1;
        public const int m_jobInternalErrorCode = -2;
        public const int m_noErrorCode = 0;

        public enum AgentState { Busy, Available, Cancelling };

        public AgentState m_state;

        private UdpClient m_discoveryClient;
        public UdpClient getUdpClient() { return m_discoveryClient; }
        private TcpListener m_listener;

        private CancellationTokenSource m_cancelTokenSource;

        private PerformanceCounter m_cpuCounter;
        private PerformanceCounter m_ramCounter;

        /// <summary>
        ///     HerdAgent class constructor
        /// </summary>
        /// <param name="cancelTokenSource"></param>
        public HerdAgent(CancellationTokenSource cancelTokenSource)
        {
            //Set invariant culture to use english notation
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            
            SetLogMessageHandler(LogMessage);
            m_cancelTokenSource = cancelTokenSource;
            m_state = AgentState.Available;
            
            //Clean-up
            m_dirPath = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location) + "/temp";
            Directory.CreateDirectory(m_dirPath);
            
            CleanLog();
            CleanTempDir();

            //Run counters BEFORE initializing static properties because we use ram counter
            m_cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            m_cpuCounter.NextValue();

            if (ArchitectureId() == PropValues.Win32 || ArchitectureId() == PropValues.Win64)
                m_ramCounter = new PerformanceCounter("Memory", "Available MBytes", true);
            else
                m_ramCounter = new PerformanceCounter("Mono Memory", "Total Physical Memory", true);
            m_ramCounter.NextValue();

            InitStaticProperties();

            LogToFile("Herd agent started");

        }

        public string DirPath { get { return m_dirPath; } }



        public void SendJobResult(CancellationToken cancelToken)
        {
            SendJobHeader(cancelToken);
            SendOutputFiles(true, cancelToken);
            SendJobFooter(cancelToken);
        }
        public async Task<bool> ReceiveJobQuery(CancellationToken cancelToken)
        {
            bool bFooterPeeked = false;
            string xmlTag = "";
            m_job.Tasks.Clear();
            m_job.InputFiles.Clear();
            m_job.OutputFiles.Clear();

            int ret = await ReceiveJobHeader(cancelToken);
            bool bret;
            do
            {
                //ReadFromStream();
                xmlTag = m_xmlStream.peekNextXMLTag();
                while (xmlTag == "")
                {
                    ret = await ReadFromStreamAsync(cancelToken);
                    xmlTag = m_xmlStream.peekNextXMLTag();
                }
                switch (xmlTag)
                {
                    case "Task": bret = await ReceiveTask(cancelToken); break;
                    case "Input": bret = await ReceiveFile(FileType.INPUT, true, true, cancelToken); break;
                    case "Output": bret = await ReceiveFile(FileType.OUTPUT, false, true, cancelToken); break;
                    case "/Job": bFooterPeeked = true; break;
                    default: LogToFile("WARNING: Unexpected xml tag received: " + xmlTag); break;
                }
            } while (!bFooterPeeked);

            LogToFile("Waiting for job footer");
            bret = await ReceiveJobFooter(cancelToken);
            LogToFile("Job footer received");

            return true;
        }


        public static Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<object>();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => { tcs.TrySetResult(null); };

            cancellationToken.Register(tcs.SetCanceled);
            return tcs.Task;
        }


        public async Task<int> RunTaskAsync(HerdTask task, CancellationToken cancelToken)
        {
            int returnCode = m_noErrorCode;
            NamedPipeServerStream pipeServer = null;
            Process myProcess = new Process();
            if (task.Pipe != "")
                pipeServer = new NamedPipeServerStream(task.Pipe);
            XMLStream xmlStream = new XMLStream();

            try
            {
                //not to read 23.232 as 23232
                Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

                myProcess.StartInfo.FileName = getCachedFilename(task.Exe);
                myProcess.StartInfo.Arguments = task.Arguments;
                myProcess.StartInfo.WorkingDirectory = Path.GetDirectoryName(myProcess.StartInfo.FileName);

                myProcess.Start();

                LogToFile("Running command: " + myProcess.StartInfo.FileName + " " + myProcess.StartInfo.Arguments);

                //addSpawnedProcessToList(myProcess);

                string xmlItem;

                if (pipeServer != null)
                {
                    pipeServer.WaitForConnection();

                    while (pipeServer.IsConnected)
                    {
                        //check if the herd agent sent us a quit message
                        //in case it did, the function will cancel the cancellation token
                        CheckCancellationRequests();

                        //check if we have been asked to cancel
                        cancelToken.ThrowIfCancellationRequested();

                        int numBytes = await xmlStream.readFromNamedPipeStreamAsync(pipeServer, cancelToken);
                        xmlItem = xmlStream.processNextXMLItem();
                        while (xmlItem != "")
                        {
                            await xmlStream.writeMessageAsync(m_tcpClient.GetStream(),
                                "<" + task.Pipe + ">" + xmlItem + "</" + task.Pipe + ">", cancelToken);

                            xmlItem = xmlStream.processNextXMLItem();
                        }
                    }
                }

                LogMessage("Named pipe has been closed for task " + task.Name);
                await WaitForExitAsync(myProcess, cancelToken);

                int exitCode = myProcess.ExitCode;
                LogMessage("Process exited in task " + task.Name + ". Return code=" + exitCode);
                //myProcess.WaitForExit();

                if (exitCode < 0)
                {
                    await xmlStream.writeMessageAsync(m_tcpClient.GetStream(),
                        "<" + task.Pipe + "><End>Error</End></" + task.Pipe + ">", cancelToken);
                    returnCode = m_jobInternalErrorCode;
                }
                else
                    await xmlStream.writeMessageAsync(m_tcpClient.GetStream(),
                        "<" + task.Pipe + "><End>Ok</End></" + task.Pipe + ">", cancelToken);
                LogMessage("Exit code: " + myProcess.ExitCode);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Thread finished gracefully");
                if (myProcess != null) myProcess.Kill();
                returnCode = m_remotelyCancelledErrorCode;
            }
            catch (AuthenticationException ex){
                LogMessage(ex.ToString());
            }
            catch (Exception ex)
            {
                LogMessage("unhandled exception in runTaskAsync()");
                LogMessage(ex.ToString());
                if (myProcess != null) myProcess.Kill();
                returnCode = m_jobInternalErrorCode;
            }
            finally
            {
                LogMessage("Task " + task.Name + " finished");
                //removeSpawnedProcessFromList(myProcess);
                if (pipeServer != null) pipeServer.Close();
            }

            return returnCode;
        }


        public async Task<int> RunJobAsync(CancellationToken cancelToken)
        {
            int returnCode = m_noErrorCode;
            try
            {
                List<Task<int>> taskList = new List<Task<int>>();
                foreach (HerdTask task in m_job.Tasks)
                    taskList.Add(RunTaskAsync(task, cancelToken));
                int[] exitCodes = await Task.WhenAll(taskList);

                if (exitCodes.Any((code) => code == m_remotelyCancelledErrorCode))
                    returnCode = m_remotelyCancelledErrorCode;
                else if (exitCodes.All((code) => code != m_noErrorCode))
                    returnCode = m_jobInternalErrorCode;

                LogMessage("All processes finished");
            }
            catch (OperationCanceledException)
            {
                returnCode = m_remotelyCancelledErrorCode;
                LogMessage("Job cancelled gracefully");
            }
            catch (Exception ex)
            {
                LogMessage(ex.ToString());
                returnCode = m_jobInternalErrorCode;
            }
            finally
            {
                m_cancelTokenSource.Dispose();
                m_cancelTokenSource = new CancellationTokenSource();
            }
            return returnCode;
        }


        public AgentState State { get { return m_state; } set { m_state = value; } }


        public string StateString()
        {
            if (m_state == AgentState.Available) return PropValues.StateAvailable;
            if (m_state == AgentState.Busy) return PropValues.StateBusy;
            if (m_state == AgentState.Cancelling) return PropValues.StateCancelling;
            return PropValues.None;
        }


         public float CurrentCpuUsage()
         {
            return m_cpuCounter.NextValue();
         }

        static public string ProcessorId()
        {
            //Using the ProcessorId doesn't work in Linux, so we will use the 1st adapter's physical address to identify the agent

            IPGlobalProperties computerProperties = IPGlobalProperties.GetIPGlobalProperties();
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();

            if (nics == null || nics.Length < 1)
                return PropValues.None;

            return nics[0].GetPhysicalAddress().ToString();
        }

        string HerdAgentVersion = PropValues.None;
        string CPUId = PropValues.None;
        int NumCPUCores = 0;
        string CPUArchitecture = PropValues.None;
        string TotalMemory = PropValues.None;

        static public string ArchitectureId()
        {
            bool is64 = Environment.Is64BitOperatingSystem;

            OperatingSystem os = Environment.OSVersion;
            PlatformID pid = os.Platform;
            switch (pid)
            {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                case PlatformID.WinCE:
                    if (is64) return PropValues.Win64;
                    else return PropValues.Win32;

                case PlatformID.Unix:
                    if (is64) return PropValues.Unix64;
                    else return PropValues.Unix32;
                default:
                    return PropValues.None;
            }


        }
        /// <summary>
        /// This method should be called once on initialization to set static properties: CUDA support
        /// , number of cores, ... No point checking them every time we get a ping from the client
        /// </summary>
        private void InitStaticProperties()
        {
            LogToFile("Initializing Herd Agent static properties:");

            //Herd Agent version
            HerdAgentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            //Number of CPU cores
            NumCPUCores = Environment.ProcessorCount;
            //Processor Id
            CPUId = ProcessorId();
            // CPU architecture
            CPUArchitecture = ArchitectureId();
            // Total installed memory
            TotalMemory = GetAvailableMemory();
            //CUDA support
            CUDAVersion= CUDASupport();

            LogToFile(AgentDescription());
        }

        string CUDAVersion = "";

        /// <summary>
        ///     Get information about CUDA installation.
        /// </summary>
        /// <returns>The CUDA version installed or -1 if none was found</returns>
        static public string CUDASupport()
        {
            string dllName = @"nvcuda.dll";
            string dir = Environment.SystemDirectory;

            try
            {
                FileVersionInfo myFileVersionInfo = null;

                var dllPath = dir + "/" + dllName;
                if (System.IO.File.Exists(dllPath))
                {
                    myFileVersionInfo = FileVersionInfo.GetVersionInfo(dllPath);
                    //This is a quick and dirty hack to get the CUDA version without loading the DLL
                    //It seems that nVidia sets the ProductName with the cuda version inside. I hope they
                    //keep doint it
                    string version = myFileVersionInfo.ProductName;
                    return new string(version.Where(c => char.IsDigit(c) || char.IsPunctuation(c)).ToArray());
                }
                else
                {
                    return PropValues.None;
                }
            }
            catch (Exception ex)
            {
                Console.Write(ex.StackTrace);
                return PropValues.None;
            }
        }


        private string GetAvailableMemory()
        {
            if (ArchitectureId() == PropValues.Win32 || ArchitectureId() == PropValues.Win64)
                return Convert.ToInt32(m_ramCounter.NextValue()).ToString() + "Mb";
            else
                return Convert.ToInt32(m_ramCounter.NextValue() / 1024 / 1024).ToString() + "Mb";
        }

        public string AgentDescription()
        {
            string description = "<" + XmlTags.HerdAgentDescription + ">"
                // HerdAgent version
                + "<" + PropNames.HerdAgentVersion + ">"
                + HerdAgentVersion
                + "</" + PropNames.HerdAgentVersion + ">"
                // CPU amount of cores
                + "<" + PropNames.ProcessorId + ">" + CPUId + "</" + PropNames.ProcessorId + ">"
                // CPU amount of cores
                + "<" + PropNames.NumCPUCores + ">" + NumCPUCores + "</" + PropNames.NumCPUCores + ">"
                // CPU architecture
                + "<" + PropNames.Architecture + ">" + CPUArchitecture + "</" + PropNames.Architecture + ">"
                // Current processor load
                + "<" + PropNames.ProcessorLoad + ">" + CurrentCpuUsage().ToString(CultureInfo.InvariantCulture) + "</" + PropNames.ProcessorLoad + ">"
                // Total installed memory
                + "<" + PropNames.TotalMemory + ">" + TotalMemory + "</" + PropNames.TotalMemory + ">"
                // CUDA support information
                + "<" + PropNames.CUDA + ">" + CUDAVersion + "</" + PropNames.CUDA + ">"
                // HerdAgent state
                + "<" + PropNames.State + ">" + StateString() + "</" + PropNames.State + ">"
                + "</" + XmlTags.HerdAgentDescription + ">";
            return description;
        }


        public void AcceptJobQuery(IAsyncResult ar)
        {
            m_tcpClient = m_listener.EndAcceptTcpClient(ar);
            m_xmlStream.resizeBuffer(m_tcpClient.ReceiveBufferSize);
            m_netStream = m_tcpClient.GetStream();
        }


        public void CheckCancellationRequests()
        {
            try
            {
                if (!m_netStream.DataAvailable) return;

                XMLStream inputXMLStream = new XMLStream();
                var bytes = m_netStream.Read(inputXMLStream.getBuffer(), inputXMLStream.getBufferOffset(),
                    inputXMLStream.getBufferSize() - inputXMLStream.getBufferOffset());

                inputXMLStream.addBytesRead(bytes);
                //we let the xmlstream object know that some bytes have been read in its buffer
                string xmlItem = inputXMLStream.peekNextXMLItem();
                if (xmlItem != "")
                {
                    string xmlItemContent = inputXMLStream.getLastXMLItemContent();
                    if (xmlItemContent == JobDispatcher.m_quitMessage)
                    {
                        inputXMLStream.addProcessedBytes(bytes);
                        inputXMLStream.discardProcessedData();
                        LogMessage("Stopping job execution");
                        m_cancelTokenSource.Cancel();
                    }
                }
            }
            catch (IOException)
            {
                LogMessage("IOException in CheckCancellationRequests()");
            }
            catch (OperationCanceledException)
            {
                LogMessage("Thread finished gracefully");
            }
            catch (ObjectDisposedException)
            {
                LogMessage("Network stream closed: async read finished");
            }
            catch (InvalidOperationException ex)
            {
                LogMessage("InvalidOperationException in CheckCancellationRequests");
                LogMessage(ex.ToString());
            }
            catch (Exception ex)
            {
                LogMessage("Unhandled exception in CheckCancellationRequests");
                LogMessage(ex.ToString());
            }
        }


        public async void CommunicationCallback(IAsyncResult ar)
        {
            if (State != AgentState.Busy)
            {
                AcceptJobQuery(ar);

                try
                {
                    State = AgentState.Busy;

                    int ret = await readAsync(m_cancelTokenSource.Token);
                    string xmlItem = m_xmlStream.processNextXMLItem();
                    string xmlItemContent;
                    int returnCode;

                    if (xmlItem != "")
                    {
                        xmlItemContent = m_xmlStream.getLastXMLItemContent();
                        if (xmlItemContent == JobDispatcher.m_acquireMessage)
                        {
                            LogMessage("Receiving job data from "
                                + m_tcpClient.Client.RemoteEndPoint.ToString());
                            bool bret = await ReceiveJobQuery(m_cancelTokenSource.Token);
                            if (bret)
                            {
                                //run the job
                                LogMessage("Job received");
                                LogMessage(m_job.ToString());
                                LogMessage("Running job");
                                returnCode = await RunJobAsync(m_cancelTokenSource.Token);

                                if (returnCode == m_noErrorCode || returnCode == m_jobInternalErrorCode)
                                {
                                    LogMessage("Job finished. Code=" + returnCode );
                                    await writeMessageAsync(JobDispatcher.m_endMessage, m_cancelTokenSource.Token, true);

                                    LogMessage("Sending job results");
                                    //we will have to enqueue async write operations to wait for them to finish before closing the tcpClient
                                    startEnqueueingAsyncWriteOps();
                                    SendJobResult(m_cancelTokenSource.Token);

                                    LogMessage("Job results sent");
                                }
                                else if (returnCode == m_remotelyCancelledErrorCode)
                                {
                                    LogMessage("The job was remotely cancelled");
                                    writeMessage(JobDispatcher.m_errorMessage, false);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage("Unhandled exception in the herd agent's communication callback function");
                    LogMessage(ex.ToString() + ex.InnerException + ex.StackTrace);
                }
                finally
                {
                    LogMessage("Waiting for queued async write operations to finish");
                    waitAsyncWriteOpsToFinish();
                    LogMessage("Closing the TCP connection");
                    m_tcpClient.Close();
                    State = AgentState.Available;

                    //try to recover
                    //start listening again
                    HerdAgentTcpState tcpState = new HerdAgentTcpState();
                    tcpState.ip = new IPEndPoint(0, 0);

                    m_listener.BeginAcceptTcpClient(CommunicationCallback, tcpState);
                }
            }
        }


        public void DiscoveryCallback(IAsyncResult ar)
        {
            IPEndPoint ip = ((HerdAgentUdpState)ar.AsyncState).ip;
            //HerdAgent herdAgent = ((HerdAgentUdpState)ar.AsyncState).herdAgent;
            try
            {
                Byte[] receiveBytes = getUdpClient().EndReceive(ar, ref ip);
                string receiveString = Encoding.ASCII.GetString(receiveBytes);

                if (receiveString == JobDispatcher.m_discoveryMessage)
                {
                    //if (getState() == AgentState.AVAILABLE)
                    {
                        //logMessage("Agent discovered by " + ip + ". Current state=" + getStateString());
                        string agentDescription = AgentDescription();
                        byte[] data = Encoding.ASCII.GetBytes(agentDescription);
                        getUdpClient().Send(data, data.Length, ip);
                    }
                    //else logMessage("Agent contacted by " + ip.ToString() + " but rejected connection because it was busy");
                }
                else LogMessage("Message received by " + ip + " not understood: " + receiveString);

                getUdpClient().BeginReceive(new AsyncCallback(DiscoveryCallback), ar.AsyncState);
            }
            catch (Exception ex)
            {
                LogMessage("Unhandled exception in DiscoveryCallback");
                LogMessage(ex.ToString());
            }
        }


        public void StartListening()
        {
            //UPD broadcast client
            m_discoveryClient = new UdpClient(JobDispatcher.m_discoveryPortHerd);
            HerdAgentUdpState state = new HerdAgentUdpState();
            IPEndPoint shepherd = new IPEndPoint(0, 0);
            state.ip = shepherd;
            // state.herdAgent = this;
            m_discoveryClient.BeginReceive(DiscoveryCallback, state);


            //TCP communication socket
            m_listener = new TcpListener(IPAddress.Any, JobDispatcher.m_comPortHerd);
            m_listener.Start();
            HerdAgentTcpState tcpState = new HerdAgentTcpState();
            tcpState.ip = shepherd;

            m_listener.BeginAcceptTcpClient(CommunicationCallback, tcpState);
        }


        public void StopListening()
        {
            m_discoveryClient.Close();
            m_listener.Stop();
            LogToFile("Herd Agent stopped");
        }

        public string GetLogFilename()
        {
            return m_dirPath + @"/log.txt";
        }

        private string m_dirPath = "";

        private object m_logFileLock = new object();

        public void CleanLog()
        {
            string logFile = GetLogFilename();
            lock (m_logFileLock)
            {
                FileStream file = File.Create(logFile);
                file.Close();
            }
        }
        private const double fileRetirementAgeInDays = 15.0;
        private void CleanDir(string dir)
        {
            //clean child files
            foreach (string file in Directory.GetFiles(dir))
            {
                double filesAge = (DateTime.Now - File.GetLastWriteTime(file)).TotalDays;
                if (filesAge > fileRetirementAgeInDays)
                {
                    LogToFile("Deleting temporal file: " + file);
                    File.Delete(file);
                }
            }
            //clean child directories
            foreach (string subDir in Directory.GetDirectories(dir))
            {
                CleanDir(subDir);
                string[] childDirs = Directory.GetDirectories(subDir);
                string[] childFiles = Directory.GetFiles(subDir);
                if (childDirs.Length == 0 && childFiles.Length == 0)
                {
                    LogToFile("Deleting temporal directory: " + subDir);
                    Directory.Delete(subDir);
                }
            }
        }
        public void CleanTempDir()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(m_dirPath))
                {
                    CleanDir(dir);
                    string[] childDirs = Directory.GetDirectories(dir);
                    string[] childFiles = Directory.GetFiles(dir);
                    if (childDirs.Length == 0 && childFiles.Length == 0)
                    {
                        LogToFile("Deleting temporal directory: " + dir);
                        Directory.Delete(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                LogToFile("Exception cleaning temporal directory");
                LogToFile(ex.ToString());
            }
        }
        public void LogToFile(string logMessage)
        {
            string logFilename = GetLogFilename();
            lock (m_logFileLock)
            {
                if (File.Exists(logFilename))
                {
                    string text = DateTime.Now.ToShortDateString() + " " +
                        DateTime.Now.ToShortTimeString() + ": " + logMessage;
                    StreamWriter w = File.AppendText(logFilename);

                    w.WriteLine(text);
                    w.Close();
                    Console.WriteLine(text);
                }
            }
        }
    }

}
