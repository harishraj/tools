﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using MoreLinq;

namespace Managed.Adb
    {
    /**
     * A Device monitor. This connects to the Android Debug Bridge and get device and debuggable
     * process information from it.
     */
    public class DeviceMonitor
        {
        //---------------------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------------------

        public IList<Device>                Devices                 { get; private set; }
        public IList<int>                   DebuggerPorts           { get; private set; }
        public Dictionary<IClient, int>     ClientsToReopen         { get; private set; }
        public bool                         IsMonitoring            { get; private set; }
        public int                          ADBConnectionAttempts   { get; private set; }
        public int                          BridgeRestartAttempts   { get; private set; }
        public bool                         HasInitialDeviceList    { get; private set; }

        private readonly AndroidDebugBridge bridge;
        private bool                        started                 { get; set; }
        private bool                        stopRequested           { get; set; }
        private Socket                      socketADB               { get; set; }
        private Thread                      deviceMonitorThread;
        private const string                loggingTag = "DeviceMonitor";
        private byte[]                      lengthBuffer = null;
        private ManualResetEventSlim        startedEvent = new ManualResetEventSlim(false);
        private ReaderWriterLock            socketLock = new ReaderWriterLock();

        //---------------------------------------------------------------------------------------------
        // Constructoin 
        // ---------------------------------------------------------------------------------------------

        public DeviceMonitor(AndroidDebugBridge bridge)
            {
            this.bridge     = bridge;
            Devices         = new List<Device>();
            DebuggerPorts   = new List<int>();
            ClientsToReopen = new Dictionary<IClient, int>();
            DebuggerPorts.Add(DdmPreferences.DebugPortBase);
            this.lengthBuffer = new byte[4];
            this.started    = false;
            this.stopRequested = false;
            }

        public void AddClientToDropAndReopen(IClient client, int port)
            {
            lock (ClientsToReopen)
                {
                Log.d(loggingTag, $"Adding {client} to list of client to reopen ({port})");
                if (!ClientsToReopen.ContainsKey(client))
                    {
                    ClientsToReopen.Add(client, port);
                    }
                }
            }

        public void Start()
            {
            // Paranoia: stop, just in case
            Stop();

            // Start the monitor thread a-going
            this.deviceMonitorThread = new Thread(new ThreadStart(DeviceMonitorLoop));
            this.deviceMonitorThread.Name = "Device List Monitor";
            this.deviceMonitorThread.Start();

            // Wait until the thread actually starts; this allows us to shut it down reliably
            // as we will actually have access to it's socket
            this.startedEvent.Wait();
            }

        // A lock for controlling access to the right to update the socket
        void AcquireSocketWriteLock()    { this.socketLock.AcquireWriterLock(-1); }
        void ReleaseSocketWriteLock()    { this.socketLock.ReleaseWriterLock();   }

        public void Stop()
            {
            if (this.stopRequested)
                {
                // Set the flag for he gets around to lookign
                this.stopRequested = true;
                
                // Close the socket to get him out of Receive() if he's there
                this.AcquireSocketWriteLock();
                try {
                    this.socketADB?.Disconnect(false);
                    }
                finally
                    {
                    this.ReleaseSocketWriteLock();    
                    }
                
                // Interrupt the thread just in case there are other waits
                this.deviceMonitorThread.Interrupt();

                this.deviceMonitorThread.Join();
                this.deviceMonitorThread = null;
                this.started = false;
                }
            }

        private void DeviceMonitorLoop()
            {
            // Right here we know that Start() hasn't yet returned
            this.started = true;
            this.startedEvent.Set();

            // Loop forever
            do
                {
                try
                    {
                    //---------------------------------------------------------------
                    // Get ourselves a socket to ADB (re)starting it as necessary
                    
                    this.AcquireSocketWriteLock();

                    // If we haven't a socket, try to open one
                    if (this.socketADB == null)
                        {
                        this.socketADB = OpenADBSocket();
                        if (this.socketADB == null)
                            {
                            // Open attempt failed. Restart ADB if we should
                            this.ADBConnectionAttempts++;
                            if (this.ADBConnectionAttempts > 10)
                                {
                                // BUG: This also starts a new device monitor
                                if (!this.bridge.Start())
                                    {
                                    this.BridgeRestartAttempts++;
                                    }
                                else
                                    {
                                    this.BridgeRestartAttempts = 0;
                                    }
                                }

                            // Wait a bit before attempting another socket open
                            this.ReleaseSocketWriteLock();
                            WaitBeforeContinue();

                            // Get out if we've been asked to
                            if (this.stopRequested)
                                return;

                            // Get the lock to re-establish state
                            this.AcquireSocketWriteLock();
                            }
                        else
                            {
                            Log.d(loggingTag, "Connected to adb for device monitoring");
                            this.ADBConnectionAttempts = 0;
                            }
                        }
                    this.ReleaseSocketWriteLock();
                    //
                    // ---------------------------------------------------------------

                    if (this.socketADB != null && !this.IsMonitoring && this.socketADB.Connected)
                        {
                        this.IsMonitoring = SendHostTrackDevices();
                        }

                    if (this.IsMonitoring)
                        {
                        // read the length of the incoming message
                        int length = ReadLength(this.socketADB, this.lengthBuffer);
                        if (length >= 0)
                            {
                            // read the incoming message
                            ProcessIncomingDeviceData(length);

                            // flag the fact that we have build the list at least once.
                            HasInitialDeviceList = true;
                            }
                        }
                    }
                catch (IOException ioe)
                    {
                    if (this.stopRequested)
                        {
                        Log.e(loggingTag, "Adb connection Error: ", ioe);
                        this.IsMonitoring = false;
                        if (this.socketADB != null)
                            {
                            try
                                {
                                this.socketADB.Close();
                                }
                            catch (IOException)
                                {
                                // we can safely ignore that one.
                                }
                            this.socketADB = null;
                            }
                        }
                    }
                catch (Exception)
                    {
                    if (this.stopRequested)
                        return;
                    }
                } 
            while (!this.stopRequested);
            }

        private void WaitBeforeContinue()
            {
            Thread.Sleep(1000);
            }

        /**
         * Ask the ADB server to inform us of the connection and disconnection of devices
         *
         * @exception   IOException Thrown when an IO failure occurred.
         *
         * @return  true if it succeeds, false if it fails.
         */
        private bool SendHostTrackDevices()
            {
            byte[] request = AdbHelper.Instance.FormAdbRequest("host:track-devices");
            if (AdbHelper.Instance.Write(this.socketADB, request) == false)
                {
                Log.e(loggingTag, "Sending Tracking request failed!");
                this.socketADB.Close();
                throw new IOException("Sending Tracking request failed!");
                }

            AdbResponse resp = AdbHelper.Instance.ReadAdbResponse(this.socketADB, false /* readDiagString */);
            if (!resp.IOSuccess)
                {
                Log.e(loggingTag, "Failed to read the adb response!");
                this.socketADB.Close();
                throw new IOException("Failed to read the adb response!");
                }

            if (!resp.Okay)
                {
                // request was refused by adb!
                Log.e(loggingTag, "adb refused request: {0}", resp.Message);
                }

            return resp.Okay;
            }

        /// <summary>
        /// Processes the incoming device data.
        /// </summary>
        /// <param name="length">The length.</param>
        private void ProcessIncomingDeviceData(int length)
            {
            List<Device> list = new List<Device>();
            if (length > 0)
                {
                byte[] buffer = new byte[length];
                string result = Read(this.socketADB, buffer);
                string[] devices = result.Split(new string[] {"\r\n", "\n"}, StringSplitOptions.RemoveEmptyEntries);
                devices.ForEach(d =>
                    {
                    try
                        {
                        var dv = Device.CreateFromAdbData(d);
                        if (dv != null)
                            {
                            list.Add(dv);
                            }
                        }
                    catch (ArgumentException ae)
                        {
                        Log.e(loggingTag, ae);
                        }
                    });
                }

            // now merge the new devices with the old ones.
            UpdateDevices(list);
            }

        private void UpdateDevices(List<Device> newDevices)
            {
            // because we are going to call mServer.deviceDisconnected which will acquire this lock
            // we lock it first, so that the AndroidDebugBridge lock is always locked first.
            lock (AndroidDebugBridge.GetLock())
                {
                lock (Devices)
                    {
                    // For each device in the current list, we look for a matching the new list.
                    // * if we find it, we update the current object with whatever new information
                    //   there is
                    //   (mostly state change, if the device becomes ready, we query for build info).
                    //   We also remove the device from the new list to mark it as "processed"
                    // * if we do not find it, we remove it from the current list.
                    //
                    // Once this is done, the new list contains device we aren't monitoring yet, so we
                    // add them to the list, and start monitoring them.

                    for (int d = 0; d < Devices.Count;)
                        {
                        Device device = Devices[d];

                        // look for a similar device in the new list.
                        int count = newDevices.Count;
                        bool foundMatch = false;
                        for (int dd = 0; dd < count; dd++)
                            {
                            Device newDevice = newDevices[dd];
                            // see if it matches in id and serial number.
                            if (Util.equalsIgnoreCase(newDevice.SerialNumber, device.SerialNumber))
                                {
                                foundMatch = true;

                                // update the state if needed.
                                if (device.State != newDevice.State)
                                    {
                                    device.State = newDevice.State;
                                    device.OnStateChanged(EventArgs.Empty);

                                    // if the device just got ready/online, we need to start monitoring it.
                                    if (device.IsOnline)
                                        {
                                        OnDeviceTransitionToOnline(device);
                                        }
                                    }

                                // remove the new device from the list since it's been used
                                newDevices.RemoveAt(dd);
                                break;
                                }
                            }

                        if (!foundMatch)
                            {
                            // the device is gone, we need to remove it, and keep current index to process the next one.
                            RemoveDevice(device);
                            if (device.State == DeviceState.Online)
                                {
                                device.State = DeviceState.Offline;
                                device.OnStateChanged(EventArgs.Empty);
                                this.bridge?.OnDeviceDisconnected(new DeviceEventArgs(device));
                                }
                            }
                        else
                            {
                            // process the next one
                            d++;
                            }
                        }

                    // At this point we should still have some new devices in newList, so we process them.
                    // These are the devices that we are not yet monitoring
                    foreach (Device newDevice in newDevices)
                        {
                        this.Devices.Add(newDevice);
                        if (newDevice.State == DeviceState.Online)
                            {
                            OnDeviceTransitionToOnline(newDevice);
                            }
                        }
                    }
                }
            newDevices.Clear();
            }

        private void OnDeviceTransitionToOnline(Device device)
            {
            this.bridge?.OnDeviceConnected(new DeviceEventArgs(device));

            if (AndroidDebugBridge.ClientSupport)
                {
                if (!StartMonitoringDevice(device))
                    {
                    Log.e(loggingTag, "Failed to start monitoring {0}", device.SerialNumber);
                    }
                }

            QueryNewDeviceForInfo(device);
            }

        /// <summary>
        /// Removes the device.
        /// </summary>
        /// <param name="device">The device.</param>
        private void RemoveDevice(Device device)
            {
            //device.Clients.Clear ( );
            Devices.Remove(device);

            Socket channel = device.ClientMonitoringSocket;
            if (channel != null)
                {
                try
                    {
                    channel.Close();
                    }
                catch (IOException)
                    {
                    // doesn't really matter if the close fails.
                    }
                }
            }

        private void QueryNewDeviceForInfo(Device device)
            {
            // TODO: do this in a separate thread.
            try
                {
                // first get the list of properties.
                if (device.State != DeviceState.Offline && device.State != DeviceState.Unknown)
                    {
                    // get environment variables
                    QueryNewDeviceForEnvironmentVariables(device);
                    // instead of getting the 3 hard coded ones, we use mount command and get them all...
                    // if that fails, then it automatically falls back to the hard coded ones.
                    QueryNewDeviceForMountingPoint(device);

                    // now get the emulator Virtual Device name (if applicable).
                    if (device.IsEmulator)
                        {
                        /*EmulatorConsole console = EmulatorConsole.getConsole ( device );
						if ( console != null ) {
							device.AvdName = console.AvdName;
						}*/
                        }
                    }
                }
            catch (IOException)
                {
                // if we can't get the build info, it doesn't matter too much
                }
            }

        private void QueryNewDeviceForEnvironmentVariables(Device device)
            {
            try
                {
                if (device.State != DeviceState.Offline && device.State != DeviceState.Unknown)
                    {
                    device.RefreshEnvironmentVariables();
                    }
                }
            catch (IOException)
                {
                // if we can't get the build info, it doesn't matter too much
                }
            }

        private void QueryNewDeviceForMountingPoint(Device device)
            {
            try
                {
                if (device.State != DeviceState.Offline && device.State != DeviceState.Unknown)
                    {
                    device.RefreshMountPoints();
                    }
                }
            catch (IOException)
                {
                // if we can't get the build info, it doesn't matter too much
                }
            }

        private bool StartMonitoringDevice(Device device)
            {
            Socket socket = OpenADBSocket();

            if (socket != null)
                {
                try
                    {
                    bool result = SendDeviceMonitoringRequest(socket, device);
                    if (result)
                        {
                        /*if ( Selector == null ) {
							StartDeviceMonitorThread ( );
						}*/

                        device.ClientMonitoringSocket = socket;

                        lock (Devices)
                            {
                            // always wakeup before doing the register. The synchronized block
                            // ensure that the selector won't select() before the end of this block.
                            // @see deviceClientMonitorLoop
                            //Selector.wakeup ( );

                            socket.Blocking = true;
                            //socket.register(mSelector, SelectionKey.OP_READ, device);
                            }

                        return true;
                        }
                    }
                catch (IOException e)
                    {
                    try
                        {
                        // attempt to close the socket if needed.
                        socket.Close();
                        }
                    catch (IOException)
                        {
                        // we can ignore that one. It may already have been closed.
                        }
                    Log.d(loggingTag, "Connection Failure when starting to monitor device '{0}' : {1}", device, e.Message);
                    }
                }

            return false;
            }

        private void StartDeviceMonitorThread()
            {
            //Selector = Selector.Open();
            Thread t = new Thread(new ThreadStart(DeviceClientMonitorLoop));
            t.Name = "Device Client Monitor";
            t.Start();
            }

        private void DeviceClientMonitorLoop()
            {
            do
                {
                try
                    {
                    // This synchronized block stops us from doing the select() if a new
                    // Device is being added.
                    // @see startMonitoringDevice()
                    lock (Devices)
                        {
                        }

                    //int count = Selector.Select ( );
                    int count = 0;

                    if (!this.stopRequested)
                        {
                        return;
                        }

                    lock (ClientsToReopen)
                        {
                        if (ClientsToReopen.Count > 0)
                            {
                            Dictionary<IClient, int>.KeyCollection clients = ClientsToReopen.Keys;
                            MonitorThread monitorThread = MonitorThread.Instance;

                            foreach (IClient client in clients)
                                {
                                Device device = client.DeviceImplementation;
                                int pid = client.ClientData.Pid;

                                monitorThread.DropClient(client, false /* notify */);

                                // This is kinda bad, but if we don't wait a bit, the client
                                // will never answer the second handshake!
                                WaitBeforeContinue();

                                int port = ClientsToReopen[client];

                                if (port == DebugPortManager.NO_STATIC_PORT)
                                    {
                                    port = GetNextDebuggerPort();
                                    }
                                Log.d("DeviceMonitor", "Reopening " + client);
                                OpenClient(device, pid, port, monitorThread);
                                device.OnClientListChanged(EventArgs.Empty);
                                }

                            ClientsToReopen.Clear();
                            }
                        }

                    if (count == 0)
                        {
                        continue;
                        }

                    /*List<SelectionKey> keys = Selector.selectedKeys();
					List<SelectionKey>.Enumerator iter = keys.GetEnumerator();

					while (iter.MoveNext()) {
							SelectionKey key = iter.next();
							iter.remove();

							if (key.isValid() && key.isReadable()) {
									Object attachment = key.attachment();

									if (attachment instanceof Device) {
											Device device = (Device)attachment;

											SocketChannel socket = device.getClientMonitoringSocket();

											if (socket != null) {
													try {
															int length = readLength(socket, mLengthBuffer2);

															processIncomingJdwpData(device, socket, length);
													} catch (IOException ioe) {
															Log.d("DeviceMonitor",
																			"Error reading jdwp list: " + ioe.getMessage());
															socket.close();

															// restart the monitoring of that device
															synchronized (mDevices) {
																	if (mDevices.contains(device)) {
																			Log.d("DeviceMonitor",
																							"Restarting monitoring service for " + device);
																			startMonitoringDevice(device);
																	}
															}
													}
											}
									}
							}
					}*/
                    }
                catch (IOException)
                    {
                    if (!this.stopRequested)
                        {
                        }
                    }
                } while (this.stopRequested);
            }

        /// <summary>
        /// Sends the device monitoring request.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="device">The device.</param>
        /// <returns></returns>
        private bool SendDeviceMonitoringRequest(Socket socket, Device device)
            {
            AdbHelper.Instance.SetDevice(socket, device);
            byte[] request = AdbHelper.Instance.FormAdbRequest("track-jdwp");
            if (!AdbHelper.Instance.Write(socket, request))
                {
                Log.e(loggingTag, "Sending jdwp tracking request failed!");
                socket.Close();
                throw new IOException();
                }
            AdbResponse resp = AdbHelper.Instance.ReadAdbResponse(socket, false /* readDiagString */);
            if (resp.IOSuccess == false)
                {
                Log.e(loggingTag, "Failed to read the adb response!");
                socket.Close();
                throw new IOException();
                }

            if (resp.Okay == false)
                {
                // request was refused by adb!
                Log.e(loggingTag, "adb refused request: " + resp.Message);
                }

            return resp.Okay;
            }

        /// <summary>
        /// Opens the client.
        /// </summary>
        /// <param name="device">The device.</param>
        /// <param name="pid">The pid.</param>
        /// <param name="port">The port.</param>
        /// <param name="monitorThread">The monitor thread.</param>
        private void OpenClient(Device device, int pid, int port, MonitorThread monitorThread)
            {
            Socket clientSocket;
            try
                {
                clientSocket = AdbHelper.Instance.CreatePassThroughConnection(AndroidDebugBridge.SocketAddress, device, pid);

                clientSocket.Blocking = true;
                }
            catch (IOException ioe)
                {
                Log.w(loggingTag, "Failed to connect to client {0}: {1}'", pid, ioe.Message);
                return;
                }

            CreateClient(device, pid, clientSocket, port, monitorThread);
            }

        /// <summary>
        /// Creates the client.
        /// </summary>
        /// <param name="device">The device.</param>
        /// <param name="pid">The pid.</param>
        /// <param name="socket">The socket.</param>
        /// <param name="debuggerPort">The debugger port.</param>
        /// <param name="monitorThread">The monitor thread.</param>
        private void CreateClient(Device device, int pid, Socket socket, int debuggerPort, MonitorThread monitorThread)
            {
            /*
			 * Successfully connected to something. Create a Client object, add
			 * it to the list, and initiate the JDWP handshake.
			 */

            Client client = new Client(device, socket, pid);

            if (client.SendHandshake())
                {
                try
                    {
                    if (AndroidDebugBridge.ClientSupport)
                        {
                        client.ListenForDebugger(debuggerPort);
                        }
                    }
                catch (IOException)
                    {
                    client.ClientData.DebuggerConnectionStatus = Managed.Adb.ClientData.DebuggerStatus.ERROR;
                    Log.e("ddms", "Can't bind to local {0} for debugger", debuggerPort);
                    // oh well
                    }

                client.RequestAllocationStatus();
                }
            else
                {
                Log.e("ddms", "Handshake with {0} failed!", client);
                /*
				 * The handshake send failed. We could remove it now, but if the
				 * failure is "permanent" we'll just keep banging on it and
				 * getting the same result. Keep it in the list with its "error"
				 * state so we don't try to reopen it.
				 */
                }

            if (client.IsValid)
                {
                device.Clients.Add(client);
                monitorThread.Clients.Add(client);
                }
            else
                {
                client = null;
                }
            }

        private int GetNextDebuggerPort()
            {
            // get the first port and remove it
            lock (DebuggerPorts)
                {
                if (DebuggerPorts.Count > 0)
                    {
                    int port = DebuggerPorts[0];

                    // remove it.
                    DebuggerPorts.RemoveAt(0);

                    // if there's nothing left, add the next port to the list
                    if (DebuggerPorts.Count == 0)
                        {
                        DebuggerPorts.Add(port + 1);
                        }

                    return port;
                    }
                }

            return -1;
            }

        /// <summary>
        /// Adds the port to available list.
        /// </summary>
        /// <param name="port">The port.</param>
        public void AddPortToAvailableList(int port)
            {
            if (port > 0)
                {
                lock (DebuggerPorts)
                    {
                    // because there could be case where clients are closed twice, we have to make
                    // sure the port number is not already in the list.
                    if (DebuggerPorts.IndexOf(port) == -1)
                        {
                        // add the port to the list while keeping it sorted. It's not like there's
                        // going to be tons of objects so we do it linearly.
                        int count = DebuggerPorts.Count;
                        for (int i = 0; i < count; i++)
                            {
                            if (port < DebuggerPorts[i])
                                {
                                DebuggerPorts.Insert(i, port);
                                break;
                                }
                            }
                        // TODO: check if we can compact the end of the list.
                        }
                    }
                }
            }

        /// <summary>
        /// Reads the length of the next message from a socket.
        /// </summary>
        /// <param name="socket">The Socket to read from.</param>
        /// <param name="buffer"></param>
        /// <returns>the length, or 0 (zero) if no data is available from the socket.</returns>
        private int ReadLength(Socket socket, byte[] buffer)
            {
            string msg = Read(socket, buffer);
            if (msg != null)
                {
                try
                    {
                    int len = int.Parse(msg, System.Globalization.NumberStyles.HexNumber);
                    return len;
                    }
                catch (FormatException)
                    {
                    // we'll throw an exception below.
                    }
                }
            //throw new IOException ( "unable to read data length" );
            // we receive something we can't read. It's better to reset the connection at this point.
            return -1;
            }

        /// <summary>
        /// Reads the specified socket.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private string Read(Socket socket, byte[] data)
            {
            int count = -1;
            int totalRead = 0;

            while (count != 0 && totalRead < data.Length)
                {
                try
                    {
                    int left = data.Length - totalRead;
                    int buflen = left < socket.ReceiveBufferSize ? left : socket.ReceiveBufferSize;

                    byte[] buffer = new byte[buflen];
                    socket.ReceiveBufferSize = buffer.Length;
                    count = socket.Receive(buffer, buflen, SocketFlags.None);
                    if (count < 0)
                        {
                        throw new IOException("EOF");
                        }
                    else if (count == 0)
                        {
                        }
                    else
                        {
                        Array.Copy(buffer, 0, data, totalRead, count);
                        totalRead += count;
                        }
                    }
                catch (SocketException sex)
                    {
                    if (sex.Message.Contains("connection was aborted"))
                        {
                        // ignore this?
                        return string.Empty;
                        }
                    else
                        {
                        throw new IOException($"No Data to read: {sex.Message}");
                        }
                    }
                }

            return data.GetString(AdbHelper.DEFAULT_ENCODING);
            }

        /**
         * Opens a socket to the ADB server.
         *
         * @return  a connected socket, or null a connection could not be obtained
         */
        private Socket OpenADBSocket()
            {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
                {
                socket.Connect(AndroidDebugBridge.SocketAddress);
                socket.NoDelay = true;
                }
            catch (IOException e)
                {
                Log.w(loggingTag, e);
                socket = null;
                }

            return socket;
            }
        }
    }
