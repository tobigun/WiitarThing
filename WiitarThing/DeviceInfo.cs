using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NintrollerLib;

namespace WiitarThing
{
    using static NativeImports;

    public class DeviceInfo
    {
        public enum BtStack
        {
            Microsoft,
            Toshiba,
            Other
        }

        public string DeviceID
        {
            get
            {
                return string.IsNullOrEmpty(DevicePath) ? InstanceGUID.ToString() : DevicePath;
            }
        }

        // For Wii/U Controllers
        public string DevicePath { get; set; }
        public ControllerType Type { get; set; }

        // For Joysticks
        public Guid InstanceGUID { get; set; } = Guid.Empty;
        public string VID { get; set; }
        public string PID { get; set; }

        public bool SameDevice(string identifier)
        {
            if (!string.IsNullOrEmpty(DevicePath))
            {
                return identifier == DevicePath;
            }
            else
            {
                return identifier == InstanceGUID.ToString();
            }
        }

        public bool SameDevice(Guid guid)
        {
            if (InstanceGUID != Guid.Empty)
            {
                return guid.Equals(InstanceGUID);
            }

            return false;
        }

        public static List<DeviceInfo> GetPaths()
        {
            var result = new List<DeviceInfo>();
            Guid guid;
            int index = 0;

            // Get GUID of the HID class
            HidD_GetHidGuid(out guid);

            // handle for HID devices
            var hDevInfo = SetupDiGetClassDevs(in guid, null, IntPtr.Zero, (uint)(DIGCF.DeviceInterface | DIGCF.Present));

            SP_DEVICE_INTERFACE_DATA diData = SP_DEVICE_INTERFACE_DATA.Create();

            // Step through all devices
            while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, in guid, index, out diData))
            {
                // Get Device Buffer Size
                SetupDiGetDeviceInterfaceDetail(hDevInfo, in diData, IntPtr.Zero, 0, out uint size, IntPtr.Zero);

                // Create Detail Struct
                SP_DEVICE_INTERFACE_DETAIL_DATA diDetail = SP_DEVICE_INTERFACE_DETAIL_DATA.Create();

                SP_DEVINFO_DATA deviceInfoData = SP_DEVINFO_DATA.Create();
                
                // Populate Detail Struct
                if (SetupDiGetDeviceInterfaceDetail(hDevInfo, in diData, ref diDetail, size, out size, out deviceInfoData))
                {
                    GetBtDeviceInfo(deviceInfoData, out string deviceName, out BtStack associatedStack);

                    ControllerType type;
                    if (IsCompatibleBluetoothDevice(deviceName, out type)
                        || IsCompatibleHidDevice(diDetail, out type))
                    {
                        result.Add(new DeviceInfo
                        {
                            DevicePath = diDetail.devicePath,
                            Type = type
                        });
                    }
                }
                else
                {
                    // Failed
                }

                index += 1;
            }

            // Clean Up
            hDevInfo.Dispose();

            return result;
        }

        const uint PID_WIIMOTE = 0x0306; // Wii Remote / Wii Remote Plus (1st gen)
        const uint PID_WIIMOTE_2ND_GEN = 0x0330; // Wii Remote Plus (2nd gen, aka "-TR") / Wii U Pro Controller

        public static bool IsCompatibleHidDevice(SP_DEVICE_INTERFACE_DETAIL_DATA diDetail, out ControllerType type)
        {
            type = ControllerType.Unknown;
            bool result = false;

            // Open read/write handle
            SafeFileHandle handle = CreateFile(diDetail.devicePath, FileAccess.ReadWrite, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, EFileAttributes.Overlapped, IntPtr.Zero);

            // Create Attributes Structure
            HIDD_ATTRIBUTES attrib = new HIDD_ATTRIBUTES();
            attrib.Size = Marshal.SizeOf(attrib);

            // Populate Attributes
            if (HidD_GetAttributes(handle, out attrib))
            {              
                // Check if this is a compatable device
                if (attrib.VendorID == 0x057e && (attrib.ProductID == PID_WIIMOTE || attrib.ProductID == PID_WIIMOTE_2ND_GEN))
                {
                    // According to WiiBrew, the Wii U Pro Controller uses the same VID/PID as the Wiimote+ (2nd gen), so we cannot differentiate them
                    type = ControllerType.Wiimote;
                    result = true;
                }
            }

            handle.Close();

            return result;
        }

        public static bool IsCompatibleBluetoothDevice(string deviceName, out ControllerType type)
        {
            type = ControllerType.Unknown;
            if (deviceName != null && deviceName.StartsWith("Nintendo RVL-CNT-01"))
            {
                type = deviceName.StartsWith("Nintendo RVL-CNT-01-UC")
                    ? ControllerType.ProController // Wii U Pro Controller
                    : ControllerType.Wiimote; // "Nintendo RVL-CNT-01" [Wii Remote / Remote Plus (1st gen)] / "Nintendo RVL-CNT-01-TR" [Wii Remote Plus (2nd gen)]
                return true;
            }
            return false;
        }

        private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new DEVPROPKEY
        {
            fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
            pid = 4
        };

        private static readonly DEVPROPKEY DEVPKEY_Device_DriverProvider = new DEVPROPKEY
        {
            // DEVPROP_TYPE_STRING
            fmtid = new Guid(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6),
            pid = 9
        };

        private static readonly Guid GUID_HID_Setup_Class = new Guid(0x745a17a0, 0x74d3, 0x11d0, 0xb6, 0xfe, 0x00, 0xa0, 0xc9, 0x0f, 0x57, 0xda);

        public static void GetBtDeviceInfo(SP_DEVINFO_DATA data, out string deviceName, out BtStack btStack)
        {
            // Assume it is the Microsoft Stack
            btStack = BtStack.Microsoft;
            deviceName = null;

            SP_DEVINFO_DATA parentData = SP_DEVINFO_DATA.Create();

            var result = CM_Get_DevNode_Status(out int status, out int problemNum, (int)data.DevInst, 0);
            if (result != 0) return; // Failed

            result = CM_Get_Parent(out uint parentDevice, data.DevInst, 0);
            if (result != 0) return; // Failed

            StringBuilder parentId = new StringBuilder(200);
            result = CM_Get_Device_ID(parentDevice, parentId, parentId.Capacity, 0);
            if (result != 0) return; // Failed
            string parentIdString = parentId.ToString();

            var parentDeviceInfo = SetupDiCreateDeviceInfoList(in GUID_HID_Setup_Class, IntPtr.Zero);
            if (parentDeviceInfo.IsInvalid) return; // Failed

            if (SetupDiOpenDeviceInfo(parentDeviceInfo, parentIdString, IntPtr.Zero, 0, out parentData))
            {
                deviceName = GetDevicePropertyString(parentDeviceInfo, parentData, DEVPKEY_Device_BusReportedDeviceDesc);

                string driverProvider = GetDevicePropertyString(parentDeviceInfo, parentData, DEVPKEY_Device_DriverProvider);
                if (driverProvider == "TOSHIBA")
                {
                    // Toshiba Stack
                    btStack = BtStack.Toshiba;
                }
            }

            parentDeviceInfo.Dispose();
        }

        private static string GetDevicePropertyString(SafeDeviceInfoListHandle devInfoHandle, SP_DEVINFO_DATA deviceInfoData, in DEVPROPKEY propertyKey)
        {
            SetupDiGetDeviceProperty(devInfoHandle, deviceInfoData, propertyKey, out ulong propertyType, null, 0, out int requiredSize, 0);
            if (requiredSize <= 0)
            {
                return null;
            }

            var buffer = new StringBuilder(requiredSize);
            if (!SetupDiGetDeviceProperty(devInfoHandle, deviceInfoData, propertyKey, out propertyType, buffer, requiredSize, out requiredSize, 0))
            {
                return null;
            }
            return buffer.ToString();
        }
    }
}
