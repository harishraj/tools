//
// ToolsLibraryHelper.cpp
//
#include "stdafx.h"

template<class T> void Zero(T* pTarget, size_t cb)
    {
    memset(pTarget, 0, cb);
    }
template<class T> void Zero(T* pTarget)
    {
    Zero(pTarget, sizeof(*pTarget));
    }

void Construct(SP_DEVINFO_DATA& result)
    {
    Zero(&result);
    result.cbSize = sizeof(SP_DEVINFO_DATA);
    }
void Construct(SP_DEVICE_INTERFACE_DATA& result)
    {
    Zero(&result);
    result.cbSize = sizeof(SP_DEVICE_INTERFACE_DATA);
    }
void Construct(SP_DEVICE_INTERFACE_DETAIL_DATA_W& result, int cbAllocated)
    {
    Zero(&result, cbAllocated);
    result.cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA);
    }
LPWSTR AllocCopyString(LPWSTR wsz)
    {
    size_t cbAlloc = (wcslen(wsz) + 1) * sizeof(wsz[0]);
    LPWSTR result = static_cast<LPWSTR>(CoTaskMemAlloc(cbAlloc));
    if (result != nullptr)
        StringCbCopy(result, cbAlloc, wsz);
    return result;
    }
HRESULT HError()
    {
    return HRESULT_FROM_WIN32(GetLastError());
    }

LPWSTR GetDeviceInstanceId(HDEVINFO hDevInfo, SP_DEVINFO_DATA& devInfoDevice)
    {
    DWORD cbRequired = 0;
    SetupDiGetDeviceInstanceIdW(hDevInfo, &devInfoDevice, nullptr, cbRequired, &cbRequired);
    LPWSTR result = static_cast<LPWSTR>(CoTaskMemAlloc(cbRequired));
    if (SetupDiGetDeviceInstanceIdW(hDevInfo, &devInfoDevice, result, cbRequired, &cbRequired))
        {
        // All is well
        }
    else
        {
        CoTaskMemFree(result);
        result = nullptr;
        }

    return result;
    }

USB_DEVICE_DESCRIPTOR GetUsbDeviceDescriptor(HDEVINFO hDevInfo, SP_DEVINFO_DATA& devInfoDevice)
    {
    USB_DEVICE_DESCRIPTOR result; Zero(&result);

    return result;
    }

struct EnumeratedUSBDevice
    {
    GUID        guidInterface;
    LPWSTR      wszInterfacePath;
    };

extern "C" __declspec(dllexport) BOOL EnumerateUSBDevices(GUID& guidInterfaceClass, EnumeratedUSBDevice** ppResult, int*pcDevices)
    {
    EnumeratedUSBDevice* pResult = nullptr;
    HRESULT hr = S_OK;
    int cDevices = 0;

    int err = 0;
    HDEVINFO hDevInfo = SetupDiGetClassDevsW(&guidInterfaceClass, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (hDevInfo != INVALID_HANDLE_VALUE)
        {
        __try
            {
            // How many results did we get?
            for (cDevices = 0 ;; cDevices++)
                {
                SP_DEVINFO_DATA devInfoInterface; Construct(devInfoInterface);
                if (SetupDiEnumDeviceInfo(hDevInfo, cDevices, &devInfoInterface))
                    {
                    }
                else
                    break;
                }

            // Allocate our return data
            size_t cbResult = cDevices * sizeof(EnumeratedUSBDevice);
            pResult = static_cast<EnumeratedUSBDevice*>(CoTaskMemAlloc(cbResult));
            Zero(pResult, cbResult);
            if (pResult != nullptr)
                {
                // Enumerate all the device interfaces that were found
                for (int iResult = 0; !hr; iResult++)
                    {
                    // Remember part of the data
                    pResult[iResult].guidInterface = guidInterfaceClass;

                    // Go on to the next device interface
                    SP_DEVINFO_DATA devInfoInterface; Construct(devInfoInterface);
                    if (SetupDiEnumDeviceInfo(hDevInfo, iResult, &devInfoInterface))
                        {
                        // Enumerate the interfaces of that device information element
                        SP_DEVICE_INTERFACE_DATA deviceInterfaceData; Construct(deviceInterfaceData);
                        for (int iInterface = 0;; iInterface++)
                            {
                            if (SetupDiEnumDeviceInterfaces(hDevInfo, &devInfoInterface, &guidInterfaceClass, iInterface, &deviceInterfaceData))
                                {
                                // Retrieve the device path of that interface and the actual device object itself
                                DWORD cbRequired;
                                SetupDiGetDeviceInterfaceDetailW(hDevInfo, &deviceInterfaceData, nullptr, 0, &cbRequired, nullptr);
                                SP_DEVICE_INTERFACE_DETAIL_DATA_W* pInterfaceDetail = static_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(CoTaskMemAlloc(cbRequired));
                                if (pInterfaceDetail != nullptr)
                                    {
                                    __try
                                        {
                                        Construct(*pInterfaceDetail, cbRequired);
                                        SP_DEVINFO_DATA devInfoDevice; Construct(devInfoDevice);
                                        if (SetupDiGetDeviceInterfaceDetailW(hDevInfo, &deviceInterfaceData, pInterfaceDetail, cbRequired, &cbRequired, &devInfoDevice))
                                            {
                                            pResult[iResult].wszInterfacePath = AllocCopyString(pInterfaceDetail->DevicePath);
                                            if (pResult[iResult].wszInterfacePath != nullptr)
                                                {
                                                HANDLE hInterfaceDevice = CreateFile(pResult[iResult].wszInterfacePath, GENERIC_WRITE, FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
                                                if (hInterfaceDevice != INVALID_HANDLE_VALUE)
                                                    {
                                                    __try
                                                        {
                                                        
                                                        }
                                                    __finally
                                                        {
                                                        CloseHandle(hInterfaceDevice);
                                                        }
                                                    }
                                                else 
                                                    hr = HError();
                                                }
                                            else
                                                hr = E_OUTOFMEMORY;

                                            // LPWSTR wszDeviceInstanceId = GetDeviceInstanceId(hDevInfo, devInfoDevice);
                                            // CoTaskMemFree(wszDeviceInstanceId);
                                            }
                                        else
                                            hr = HError();
                                        }
                                    __finally
                                        {
                                        CoTaskMemFree(pInterfaceDetail);
                                        }
                                    }
                                else
                                    hr = E_OUTOFMEMORY;
                                }
                            else
                                break; // interface enumeration complete
                            }
                        }
                    else
                        break;  // device enumeration complete
                    }
                }
            else if (cDevices != 0)
                {
                hr = E_OUTOFMEMORY;
                }
            }
        __finally
            {
            // Clean up the device enumeration
            SetupDiDestroyDeviceInfoList(hDevInfo);
            }
        }
    else
        err = GetLastError();    
    
    if (nullptr== pResult)
        cDevices = 0;

    *pcDevices = cDevices;
    *ppResult  = pResult;
    return !hr;
    }