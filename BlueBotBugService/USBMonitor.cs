﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Org.SwerveRobotics.Tools.Library.WIN32;

namespace Org.SwerveRobotics.Tools.Library
    {
    public interface ITracer
        { 
        void Trace(string format, params object[] args);
        void Trace(string message, USBDeviceInterface device);
        }

    public unsafe class DeviceEventArgs : EventArgs
        {
        public DEV_BROADCAST_HDR* pHeader;
        }
    public unsafe class DeviceEventArgsCancel : DeviceEventArgs
        {
        public bool Cancel = false;
        }

    public interface IDeviceEvents
        {
        event EventHandler<DeviceEventArgs>       DeviceArrived;
        event EventHandler<DeviceEventArgsCancel> DeviceQueryRemove;
        event EventHandler<DeviceEventArgs>       DeviceQueryRemoveFailed;
        event EventHandler<DeviceEventArgs>       DeviceRemovePending;
        event EventHandler<DeviceEventArgs>       DeviceRemoveComplete;
        event EventHandler<DeviceEventArgs>       DeviceTypeSpecific;
        event EventHandler<DeviceEventArgs>       DeviceCustomEvent;
        event EventHandler<DeviceEventArgs>       DeviceUserDefined;
        event EventHandler<CancelEventArgs>       DeviceQueryChangeConfig;
        event EventHandler<EventArgs>             DeviceConfigChanged;
        event EventHandler<EventArgs>             DeviceConfigChangeCancelled;
        event EventHandler<EventArgs>             DeviceDevNodesChanged;
        }

    /// <summary>
    /// A USBDeviceInformationElement represents a USB device information element
    /// </summary>
    /// Helpful links:
    ///     device id:               https://msdn.microsoft.com/en-us/library/windows/hardware/ff537109(v=vs.85).aspx
    ///     device information sets: https://msdn.microsoft.com/EN-US/library/windows/hardware/ff541247(v=vs.85).aspx
    public class USBDeviceInformationElement
        {
        //-----------------------------------------------------------------------------------------
        // State
        //-----------------------------------------------------------------------------------------

        public string                       DeviceInstanceId = null;
        public List<USBDeviceInterface>     Interfaces = new List<USBDeviceInterface>();
        }


    public class USBDeviceInterface
        {
        //-----------------------------------------------------------------------------------------
        // State
        //-----------------------------------------------------------------------------------------

        public readonly Guid     GuidDeviceInterface;
        public readonly string   DeviceInterfacePath;

        //-----------------------------------------------------------------------------------------
        // Construction
        //-----------------------------------------------------------------------------------------

        public USBDeviceInterface()
            {
            this.GuidDeviceInterface = Guid.Empty;
            this.DeviceInterfacePath = null;
            }

        public unsafe USBDeviceInterface(bool deviceAdded, DEV_BROADCAST_DEVICEINTERFACE_W* pintf) : this(deviceAdded, pintf->dbcc_classguid, pintf->dbcc_name)
            {
            }

        public USBDeviceInterface(bool deviceAdded, Guid interfaceGuid, string deviceInterfacePath)
            {
            this.GuidDeviceInterface = interfaceGuid;
            this.DeviceInterfacePath = deviceInterfacePath;
            }
        }


    public class USBMonitor : IDisposable
        {
        //-----------------------------------------------------------------------------------------
        // State
        //-----------------------------------------------------------------------------------------

        IDeviceEvents       eventRaiser = null;
        ITracer             tracer      = null;
        bool                started     = false;
        IntPtr              notificationHandle;
        bool                notificationHandleIsService;
        readonly object     traceLock = new object();

        readonly object                                             theLock = new object();
        IDictionary<Guid, IDictionary<string, USBDeviceInterface>>  mpGuidDevices = null;
        IDictionary<string, USBDeviceInterface>                     mpNameDevice  = null;
        List<Guid>                                                  deviceInterfacesOfInterest = null;
        List<IntPtr>                                                deviceNotificationHandles = null;

        public EventHandler<USBDeviceInterface> OnDeviceOfInterestArrived;
        public EventHandler<USBDeviceInterface> OnDeviceOfInterestRemoved;

        //-----------------------------------------------------------------------------------------
        // Construction
        //-----------------------------------------------------------------------------------------

        public USBMonitor(IDeviceEvents eventRaiser, ITracer tracer, IntPtr notificationHandle, bool notificationHandleIsService)
            {
            this.eventRaiser = eventRaiser;
            this.tracer = tracer;
            this.notificationHandle = notificationHandle;
            this.notificationHandleIsService = notificationHandleIsService;
            this.Initialize();
            }

        ~USBMonitor()
            {
            this.Dispose(false);
            }

        void IDisposable.Dispose()
            {
            this.Dispose(true);
            GC.SuppressFinalize(this);
            }

        void Initialize()
            {
            lock (theLock)
                {
                this.mpGuidDevices = new Dictionary<Guid, IDictionary<string, USBDeviceInterface>>();
                this.mpNameDevice  = this.NewMapStringToDevice();
                this.deviceInterfacesOfInterest = new List<Guid>();
                this.deviceNotificationHandles = new List<IntPtr>();
                }
            this.started = false;
            }

        IDictionary<string, USBDeviceInterface> NewMapStringToDevice()
            {
            return new Dictionary<string, USBDeviceInterface>(StringComparer.InvariantCultureIgnoreCase);
            }

        public virtual void Dispose(bool fromUserCode)
            {
            if (fromUserCode)
                {
                // Called from user's code. Can / should cleanup managed objects
                }

            // Called from finalizers (and user code). Avoid referencing other objects
            this.ReleaseDeviceNotificationHandles();
            }

        //-----------------------------------------------------------------------------------------
        // Device notification management
        //-----------------------------------------------------------------------------------------

        public void AddDeviceInterfaceOfInterest(Guid guid)
            {
            lock (theLock)
                {
                this.deviceInterfacesOfInterest.Add(guid);
                }

            if (this.started)
                {
                GetDeviceNotificationsFor(guid);
                // FindExistingDevices(guid);
                }
            }

        void GetDeviceNotificationsFor(Guid guidDevInterface)
            {
            lock (theLock)
                {
                DEV_BROADCAST_DEVICEINTERFACE_MANAGED filter = new DEV_BROADCAST_DEVICEINTERFACE_MANAGED();
                filter.Initialize(guidDevInterface);

                IntPtr hDeviceNotify = RegisterDeviceNotification(this.notificationHandle, filter, this.notificationHandleIsService ? DEVICE_NOTIFY_SERVICE_HANDLE : DEVICE_NOTIFY_WINDOW_HANDLE);
                ThrowIfFail(hDeviceNotify);

                this.deviceNotificationHandles.Add(hDeviceNotify);
                }
            }

        void ReleaseDeviceNotificationHandles()
            {
            lock (theLock)
                {
                foreach (IntPtr hDeviceNotify in this.deviceNotificationHandles)
                    {
                    UnregisterDeviceNotification(hDeviceNotify);
                    }
                this.deviceNotificationHandles = new List<IntPtr>();
                }
            }

        public void Start()
            {
            try
                {
                this.eventRaiser.DeviceArrived        += OnDeviceArrived;
                this.eventRaiser.DeviceRemoveComplete += OnDeviceRemoveComplete;

                foreach (Guid guid in this.deviceInterfacesOfInterest)
                    {
                    GetDeviceNotificationsFor(guid);
                    // FindExistingDevices(guid);
                    }
                
                this.started = true;
                }
            catch (Exception)
                {
                Stop();
                throw;
                }

            }

        public void Stop()
            {
            this.started = false;

            this.ReleaseDeviceNotificationHandles();

            this.eventRaiser.DeviceArrived        -= OnDeviceArrived;
            this.eventRaiser.DeviceRemoveComplete -= OnDeviceRemoveComplete;
            }


        //-----------------------------------------------------------------------------------------
        // Device Management
        //-----------------------------------------------------------------------------------------

        public unsafe void AddDeviceIfNecessary(DEV_BROADCAST_DEVICEINTERFACE_W* pintf)
            {
            AddDeviceIfNecessary(new USBDeviceInterface(true, pintf));
            }

        public unsafe bool RemoveDeviceIfNecessary(DEV_BROADCAST_DEVICEINTERFACE_W* pintf)
            {
            return RemoveDeviceIfNecessary(new USBDeviceInterface(false, pintf));
            }

        public void AddDeviceIfNecessary(USBDeviceInterface device)
            {
            lock (theLock)
                {
                if (this.deviceInterfacesOfInterest.Contains(device.GuidDeviceInterface))
                    {
                    if (!this.mpNameDevice.ContainsKey(device.DeviceInterfacePath))
                        {
                        this.mpNameDevice[device.DeviceInterfacePath] = device;
                        if (!this.mpGuidDevices.ContainsKey(device.GuidDeviceInterface))
                            {
                            this.mpGuidDevices[device.GuidDeviceInterface] = this.NewMapStringToDevice();
                            }
                        this.mpGuidDevices[device.GuidDeviceInterface][device.DeviceInterfacePath] = device;
                        Trace("added", device);
                        this.OnDeviceOfInterestArrived.Invoke(null, device);
                        }
                    }
                }
            }

        public bool RemoveDeviceIfNecessary(USBDeviceInterface device)
            {
            lock (theLock)
                {
                if (this.mpNameDevice.Remove(device.DeviceInterfacePath))
                    {
                    this.mpGuidDevices[device.GuidDeviceInterface].Remove(device.DeviceInterfacePath);
                    Trace("removed", device);
                    this.OnDeviceOfInterestRemoved.Invoke(null, device);
                    return true;
                    }
                }
            return false;
            }

        //-----------------------------------------------------------------------------------------
        // Scanning
        //-----------------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        unsafe struct EnumeratedUSBDevice
            {
            public Guid     guidInterface;
            public char*    wszInterfacePath;
            };


        static class ToolsLibraryHelper
            {
            [DllImport("ToolsLibraryHelper.dll", CharSet = CharSet.Unicode)] public unsafe static extern 
            bool EnumerateUSBDevices(ref Guid guidInterfaceClass, out EnumeratedUSBDevice* pDevices, out int cDevices);
            }

        //  “adb shell netcfg” | qgrep -y wlan
        // adb shell ifconfig wlan0

        unsafe void FindExistingDevices(Guid guidInterfaceClass)
            {
            List<USBDeviceInformationElement> devices = new List<USBDeviceInformationElement>();

            int cDevices = 0;
            EnumeratedUSBDevice* pDevices = null;
            if (ToolsLibraryHelper.EnumerateUSBDevices(ref guidInterfaceClass, out pDevices, out cDevices))
                {
                for (int iDevice = 0; iDevice < cDevices; iDevice++)
                    {
                    string interfacePath = Marshal.PtrToStringUni(new IntPtr(pDevices[iDevice].wszInterfacePath));
                    }
                }

            // Free up everything, always
            for (int iDevice = 0; iDevice < cDevices; iDevice++)
                {
                Marshal.FreeCoTaskMem(new IntPtr(pDevices[iDevice].wszInterfacePath));
                }
            Marshal.FreeCoTaskMem(new IntPtr(pDevices));
            }


        //-----------------------------------------------------------------------------------------
        // Win32 Events
        //-----------------------------------------------------------------------------------------

        unsafe void OnDeviceArrived(object sender, DeviceEventArgs args)
            {
            if (args.pHeader->dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE)
                {
                DEV_BROADCAST_DEVICEINTERFACE_W* pintf = (DEV_BROADCAST_DEVICEINTERFACE_W*)args.pHeader;
                // Trace(pintf);
                FindExistingDevices(pintf->dbcc_classguid);
                this.AddDeviceIfNecessary(new USBDeviceInterface(true, pintf));
                }
            }

        unsafe void OnDeviceRemoveComplete(object sender, DeviceEventArgs args)
            {
            if (args.pHeader->dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE)
                {
                DEV_BROADCAST_DEVICEINTERFACE_W* pintf = (DEV_BROADCAST_DEVICEINTERFACE_W*)args.pHeader;
                // Trace(pintf);
                this.RemoveDeviceIfNecessary(new USBDeviceInterface(false, pintf));
                }
            }

        unsafe void Trace(DEV_BROADCAST_DEVICEINTERFACE_W* pintf)
            {
            lock (traceLock)
                {
                this.tracer.Trace("    pintf->size={0}", pintf->dbcc_size);
                this.tracer.Trace("    pintf->DevicePath={0}", pintf->dbcc_name);
                this.tracer.Trace("    pintf->guid={0}", pintf->dbcc_classguid);
                }
            }

        void Trace(string message, USBDeviceInterface device)
            {
            lock (traceLock)
                {
                this.tracer.Trace("{0}: ", message);
                this.tracer.Trace("    DevicePath={0}", device.DeviceInterfacePath);
                this.tracer.Trace("    guid={0}", device.GuidDeviceInterface);
                }
            }
        }
    }

