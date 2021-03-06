﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Org.SwerveRobotics.Tools.ManagedADB.Exceptions;
using Org.SwerveRobotics.Tools.Util;

namespace Org.SwerveRobotics.Tools.ManagedADB
    {
    /**
     * A Device monitor. This connects to the Android Debug Bridge and get device and debuggable
     * process information from it.
     */
    public class DeviceTracker : IDisposable
        {
        //---------------------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------------------

        public IList<Device>                Devices                 { get; private set; }
        public bool                         IsTrackingDevices       { get; private set; }
        public bool                         HasDeviceList           { get; private set; }

        private readonly AndroidDebugBridge bridge;
        private int                         serverFailedConnects;
        private int                         serverRestarts;
        private Socket                      socketTrackDevices;
        private HandshakeThreadStarter      threadStarter;
        private const string                loggingTag   = "DeviceMonitor";
        private ReaderWriterLock            socketLock   = new ReaderWriterLock();
        private bool                        disposed     = false;

        //---------------------------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------------------------

        public DeviceTracker(AndroidDebugBridge bridge)
            {
            this.bridge        = bridge;
            this.Devices       = new List<Device>();
            this.serverFailedConnects = 0;
            this.serverRestarts = 0;
            this.threadStarter = new HandshakeThreadStarter("Device List Monitor", DeviceTrackingThread);
            }

        ~DeviceTracker()
            {
            Dispose(false);
            }

        public void Dispose()
            {
            Dispose(true);
            GC.SuppressFinalize(this);
            }

        protected virtual void Dispose(bool notFromFinalizer)
            {
            if (!this.disposed)
                {
                this.disposed = true;
                if (notFromFinalizer)
                    {
                    }
                this.threadStarter?.Dispose();
                this.threadStarter = null;
                }
            }

        //---------------------------------------------------------------------------------------------
        // Tracking
        // ---------------------------------------------------------------------------------------------

        public void StartDeviceTracking()
            {
            // Paranoia: stop, just in case
            StopDeviceTracking();

            // Start the monitor thread a-going
            this.threadStarter.Start();
            }

        // A lock for controlling access to the right to set the socket variable
        void AcquireSocketLock()    { this.socketLock.AcquireWriterLock(-1); }
        void ReleaseSocketLock()    { this.socketLock.ReleaseWriterLock();   }

        // never throws
        public void StopDeviceTracking()
            {
            if (this.threadStarter.IsStarted)
                {
                Log.d(loggingTag, "requesting device tracking stop...");

                // Tell the thread we want it to stop. Take the socket lock
                // to synchronize with OpenSocketIfNecessary.
                this.AcquireSocketLock();
                try {
                    this.threadStarter.RequestStop();
                    }
                finally
                    {
                    this.ReleaseSocketLock();
                    }
                
                // Close the socket to get him out of Receive() if he's there
                this.CloseSocket(ref socketTrackDevices);
                
                // Interrupt the thread just in case there are other waits
                this.threadStarter.Stop();

                Log.d(loggingTag, "...device tracking stop request complete");
                }
            }

        /**
         * Try, only once, to get a socket to the ADB server. If we can't connect, then
         * (perhaps) restart the server.
         *
         * @return  true if we opened a *new* socket
         */
        bool OpenSocketIfNecessary(HandshakeThreadStarter starter)
        // RULE: We NEVER create a new socket if a stop has been requested
            {
            bool result = false;
            this.AcquireSocketLock();
            try
                {
                // If we haven't a socket, try to open one
                if (this.socketTrackDevices == null || !this.socketTrackDevices.Connected)
                    {
                    CloseSocket(ref this.socketTrackDevices);
                    this.socketTrackDevices = ConnectToServer();
                    //
                    if (this.socketTrackDevices == null)
                        {
                        // Connect attempt failed. Restart the server if we can
                        this.serverFailedConnects++;
                        if (this.serverFailedConnects > 0)
                            {
                            this.serverRestarts++;

                            if (starter.StopRequested) return result;
                            this.bridge.KillServer();               // takes seconds

                            if (starter.StopRequested) return result;
                            this.bridge.EnsureServerStarted();      // takes seconds

                            if (starter.StopRequested) return result;
                            }

                        if (this.serverRestarts > 1)
                            {
                            // Wait a bit before attempting another socket open
                            this.ReleaseSocketLock();
                            Log.d(loggingTag, "sleeping 1s");
                            Thread.Sleep(1000);
                            this.AcquireSocketLock();
                            }
                        }
                    else
                        {
                        result = true;
                        Log.d(loggingTag, "Connected to adb for device monitoring");
                        this.serverFailedConnects = 0;
                        this.serverRestarts = 0;
                        }
                    }
                }
            finally
                {
                this.ReleaseSocketLock();
                }
            return result;
            }

        private Socket ConnectToServer()
        // Note: does NOT throw on error; returns null instead
            {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
                {
                socket.Connect(AndroidDebugBridge.AdbServerSocketAddress);
                socket.NoDelay = true;
                }
            catch (Exception)
                {
                this.CloseSocket(ref socket, false);
                }
            return socket;
            }

        /** Close the socket, which may be null. If asked to, take the socket lock while doing so. */
        void CloseSocket(ref Socket socket, bool takeLock=true)
            {
            try {
                try {
                    if (takeLock) this.AcquireSocketLock();
                    socket?.Close();
                    socket = null;
                    }
                finally
                    {
                    if (takeLock) this.ReleaseSocketLock();
                    }
                }
            catch (Exception)
                {
                // Don't actually know if anything ever throw on a close, 
                // but we'll ignore it, as we're just try to close, dang it
                }
            }

        void DeviceTrackingThread(HandshakeThreadStarter starter)
        // RULE: Once a stop is requested, we NEVER create a new socket.
        // NOTE: With that rule in place, we probably could now get by w/o doing a handshake. 
        //       The issue was that our (old) code here was racing with StopDeviceTrackign(), the
        //       former creating a socket and the latter closing it to wake us up. If that happened
        //       in the wrong order, we would never wake up.
            {
            Log.d(loggingTag, "::: DeviceTrackingThread started :::");

            // Right here we know that Start() hasn't yet returned. Do the interlock and let it return.
            starter.DoHandshake();

            // Loop until asked to stop. Do that even in the face of failures and exceptions
            while (!starter.StopRequested)
                {
                try
                    {
                    if (OpenSocketIfNecessary(starter))
                        {
                        // Opened a new socket. Ask the ADB server to give us device notifications
                        this.IsTrackingDevices = RequestDeviceNotifications();
                        }

                    if (this.IsTrackingDevices)
                        {
                        // read the length of the incoming message
                        int length = ReadLength(this.socketTrackDevices, new byte[4]);
                        if (length >= 0)
                            {
                            // read the incoming message
                            ProcessTrackingDevicesNotification(length);

                            // flag the fact that we have build the list at least once.
                            this.HasDeviceList = true;
                            }
                        }
                    }
                catch (Exception e)
                    {
                    Log.w(loggingTag, $"exception in DeviceTrackingThread: {e.Message}");
                    this.IsTrackingDevices = false;
                    CloseSocket(ref this.socketTrackDevices);
                    }
                } 

            Log.d(loggingTag, "::: DeviceTrackingThread stopped :::");
            }

        //---------------------------------------------------------------------------------------------------------------
        // Device tracking
        //---------------------------------------------------------------------------------------------------------------

        #region Device Tracking        
        /**
         * Ask the ADB server to inform us of the connection and disconnection of devices
         *
         * @exception   IOException Thrown when an IO failure occurred.
         *
         * @return  true if it succeeds, false if it fails.
         */
        private bool RequestDeviceNotifications()
            {
            byte[] request = AdbHelper.Instance.FormAdbRequest("host:track-devices");
            AdbHelper.Instance.Write(this.socketTrackDevices, request);

            AdbResponse resp = AdbHelper.Instance.ReadAdbResponse(this.socketTrackDevices);
            if (!resp.IOSuccess)
                {
                Log.e(loggingTag, "Failed to read the adb response!");
                this.CloseSocket(ref this.socketTrackDevices);
                throw new IOException("Failed to read the adb response!");
                }

            if (!resp.Okay)
                {
                // request was refused by adb!
                Log.e(loggingTag, "adb refused request: {0}", resp.Message);
                }

            return resp.Okay;
            }

        private void ProcessTrackingDevicesNotification(int length)
            {
            List<Device> currentDevices = new List<Device>();
            if (length > 0)
                {
                byte[] buffer = new byte[length];
                string result = ReadString(this.socketTrackDevices, buffer);
                string[] devices = result.Split(new string[] {"\r\n", "\n"}, StringSplitOptions.RemoveEmptyEntries);
                foreach (string deviceData in devices)
                    {
                    try
                        {
                        Device device = Device.CreateFromAdbData(deviceData);
                        if (device != null)
                            {
                            currentDevices.Add(device);
                            }
                        }
                    catch (ArgumentException ae)
                        {
                        Log.e(loggingTag, ae);
                        }
                    }
                }

            UpdateDevices(currentDevices);
            }

        /**
         * Updates our understanding of the set of current devices based on a report
         * of the now-current set.
         *
         * @param   currentDevices  the report of the current devices
         */
        private void UpdateDevices(List<Device> newCurrentDevices)
            {
            //Log.d(loggingTag, "---- ADB reports current devices -----");
            //foreach (Device device in newCurrentDevices)
            //    Log.d(loggingTag, $"   device:{device.SerialNumber}");
            //Log.d(loggingTag, "----       end report            -----");

            lock (this.Devices)
                {
                // For each device in the existing list, we look for a match in the new current list.
                // * if we find it, we update the existing object with whatever new information
                //   there is (mostly state change, if the device becomes ready, we query for build info).
                //   We also remove the device from the new current list to mark it as "processed"
                // * if we do not find it, we remove it from our existing list.
                //
                // Once this is done, the new current list contains device we aren't tracking yet, so we
                // add them to the list

                for (int d = 0; d < this.Devices.Count;)
                    {
                    Device device = this.Devices[d];

                    // look for a similar device in the new list.
                    int count = newCurrentDevices.Count;
                    bool foundMatch = false;
                    for (int dd = 0; dd < count; dd++)
                        {
                        Device newDevice = newCurrentDevices[dd];
                        // see if it matches in serial number
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
                            newCurrentDevices.RemoveAt(dd);
                            break;
                            }
                        }

                    if (!foundMatch)
                        {
                        // the device is gone, we need to remove it, and keep current index to process the next one.
                        this.Devices.Remove(device);
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
                foreach (Device newDevice in newCurrentDevices)
                    {
                    this.Devices.Add(newDevice);
                    if (newDevice.State == DeviceState.Online)
                        {
                        OnDeviceTransitionToOnline(newDevice);
                        }
                    }
                }
            }

        // Do what we need to do when we detect a device making its transition to the online state
        private void OnDeviceTransitionToOnline(Device device)
            {
            device.RefreshFromDevice();
            this.bridge?.OnDeviceConnected(new DeviceEventArgs(device));
            }

        #endregion

        //---------------------------------------------------------------------------------------------------------------
        // Utility
        //---------------------------------------------------------------------------------------------------------------

        private int ReadLength(Socket socket, byte[] buffer)
            {
            string msg = ReadString(socket, buffer);
            return int.Parse(msg, System.Globalization.NumberStyles.HexNumber);
            }

        private string ReadString(Socket socket, byte[] data)
            {
            AdbHelper.Instance.Read(socket, data);
            return data.GetString(AdbHelper.DEFAULT_ENCODING);
            }
        }
    }
