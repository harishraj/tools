﻿/*
 *  04-Oct-2013 Rick Rigby, rmrigby@gmail.com
 *  RefreshTree() will enumerate the USB and call the Action<> routine if
 *  the specified VID and list of PID's are found. The Action<> routine
 *  will be called only once for the specific USB DevicePath passed in.
 *  This is intended to work with the WM_DEVICECHANGE message, which gives
 *  the DevicePath of any USB device plugged in.
 */

using System;
using System.Collections.Generic;
using NativeUsbLib;

namespace UsbEnumeration
    {
    public class UsbTree
        {
        private readonly Action<bool, ushort, ushort, string, int, int, string, string> _reportDevice; // VID, PID, serial#, hub#, port#, devicePath, usbDevicePath
        public const ushort VendorId = 0x0123; // VID of interest

        private readonly UsbBus _usbBus = new UsbBus();
        private UsbTreeNode _usbRoot;

        public UsbTree(Action<bool, ushort, ushort, string, int, int, string, string> reportDevice)
            {
            _reportDevice = reportDevice;
            }

        // 25-Jul-2013 added targetDeviceName to specify which device was just plugged-in
        public void RefreshTree(bool deviceAdded, string targetDeviceName)
            {
            _usbRoot = new UsbTreeNode
                {
                Description = "root",
                Device = null,
                Type = DeviceTyp.RootHub,
                Parent = null,
                NodeText = "Root",
                Children = new List<UsbTreeNode>()
                };

            ReScanUsbBus();
            WalkTheBus(VendorId, deviceAdded, targetDeviceName);
            }

        private void WalkTheBus(ushort vendorId, bool deviceAdded, string deviceName)
            {
            ProcessNode(_usbRoot, vendorId, deviceAdded, deviceName);
            }

        private void ProcessNode(UsbTreeNode node, ushort vendorId, bool deviceAdded, string deviceName)
            {
            CheckNode(node, vendorId, deviceAdded, deviceName);
            foreach (var n in node.Children)
                ProcessNode(n, vendorId, deviceAdded, deviceName);
            }

        private void CheckNode(UsbTreeNode usbNode, ushort vendorId, bool deviceAdded, string targetDeviceName)
            {
            if (usbNode.Type == DeviceTyp.Device || usbNode.Type == DeviceTyp.Hub)
                {
                var device = usbNode.Device;

                //if (device != null)
                //    Debug.WriteLine(string.Format("targetDevice:{0}\nDevicePath:{1}\nUsbDevicePath:{2}",
                //                                  targetDeviceName,
                //                                  !String.IsNullOrEmpty(device.DevicePath)
                //                                            ? device.DevicePath
                //                                            : "(null)",
                //                                  !String.IsNullOrEmpty(device.UsbDevicePath)
                //                                            ? device.UsbDevicePath
                //                                            : "(null)"));

                targetDeviceName = targetDeviceName.ToLower();

                if (device != null && device.DeviceDescriptor != null &&
                    (vendorId == 0 || device.DeviceDescriptor.idVendor == vendorId &&
                     (targetDeviceName.Length == 0 ||
                      device.UsbDevicePath.ToLower() == targetDeviceName))) // 25-Jul key on USB device just plugged in only
                    {
                    if (device.IsConnected && device.DeviceDescription != null)
                        {
                        var parent = usbNode.Parent;
                        if (parent.Type != DeviceTyp.Hub && parent.Type != DeviceTyp.RootHub)
                            {
                            }
                        else
                            {
                            _reportDevice(deviceAdded,
                                device.DeviceDescriptor.idVendor,
                                device.DeviceDescriptor.idProduct,
                                device.SerialNumber,
                                parent.Device.AdapterNumber,
                                device.AdapterNumber,
                                device.DevicePath,
                                device.UsbDevicePath);
                            }
                        }
                    }
                }
            }

        private void ReScanUsbBus()
            {
            if (_usbRoot.Children.Count > 0)
                _usbRoot.Children.Clear();

            _usbBus.Refresh();
            foreach (var controller in _usbBus.Controller)
                ShowController(_usbRoot, controller);
            }

        private void ShowController(UsbTreeNode node, UsbController controller)
            {
            if (controller != null)
                {
                //var usbNode = new UsbTreeNode(controller.DeviceDescription, controller,
                //                                      DeviceTyp.Controller, null);
                var usbNode = new UsbTreeNode
                    {
                    Description = controller.DeviceDescription,
                    Device = controller,
                    Type = DeviceTyp.Controller,
                    Parent = null
                    };
                usbNode.NodeText = controller.DeviceDescription;
                node.Children.Add(usbNode);

                foreach (UsbHub hub in controller.Hubs)
                    {
                    ShowHub(usbNode, hub);
                    }
                }
            }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="node">"Visual tree"</param>
        /// <param name="hub">The UsbHub</param>
        private void ShowHub(UsbTreeNode node, UsbHub hub)
            {
            if (hub != null)
                {
                UsbTreeNode usbNode = null;
                if (hub.IsRootHub)
                    {
                    //usbNode = new UsbTreeNode(hub.DeviceDescription, hub, DeviceTyp.RootHub, null);
                    usbNode = new UsbTreeNode
                        {
                        Description = hub.DeviceDescription,
                        Device = hub,
                        Type = DeviceTyp.RootHub,
                        Parent = null
                        };
                    }
                else
                    {
                    //usbNode = new UsbTreeNode(hub.DeviceDescription, hub, DeviceTyp.Hub, node);
                    usbNode = new UsbTreeNode
                        {
                        Description = hub.DeviceDescription,
                        Device = hub,
                        Type = DeviceTyp.Hub,
                        Parent = node
                        };
                    usbNode.NodeText = "Port[" + hub.AdapterNumber + "] DeviceConnected: " + hub.DeviceDescription;
                    }
                node.Children.Add(usbNode);

                foreach (Device device in hub.Devices)
                    {
                    ShowDevice(usbNode, device);
                    }
                }
            }

        private void ShowDevice(UsbTreeNode node, Device device)
            {
            if (device != null)
                {
                if (device is UsbHub)
                    {
                    ShowHub(node, (UsbHub) device);
                    }
                else
                    {
                    //var usbNode = new UsbTreeNode(device.DeviceDescription, device, DeviceTyp.Device, node);
                    var usbNode = new UsbTreeNode
                        {
                        Description = device.DeviceDescription,
                        Device = device,
                        Type = DeviceTyp.Device,
                        Parent = node
                        };
                    string s = "Port[" + device.AdapterNumber + "]";
                    if (device.IsConnected)
                        {
                        s += " DeviceConnected: " + device.DeviceDescription;
                        }
                    else
                        {
                        s += " NoDeviceConnected";
                        }

                    usbNode.NodeText = s;
                    node.Children.Add(usbNode);
                    }
                }
            }
        }
    }
