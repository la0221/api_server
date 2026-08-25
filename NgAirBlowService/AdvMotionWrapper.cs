using System;
using Advantech.Motion;

namespace NgAirBlowService;

public static class AdvMotionWrapper
{
    public const uint MaxDevices = 16;
    public const ushort RingNo = 0;

    public static int GetAvailableDevs(DEV_LIST[] devList, uint maxDevs, ref uint count)
    {
        return (int)Motion.mAcm_GetAvailableDevs(devList, maxDevs, ref count);
    }

    public static int DevOpen(uint devNum, ushort mode, ref IntPtr handle)
    {
        return (int)Motion.mAcm_DevOpen(devNum, mode, ref handle);
    }

    public static int DevClose(ref IntPtr handle)
    {
        return (int)Motion.mAcm_DevClose(ref handle);
    }

    public static int DaqDoGetBit(IntPtr handle, ushort ring, ushort slave, ushort channel, ref byte value)
    {
        return (int)Motion.mAcm_DaqDoGetBitEx(handle, ring, slave, channel, ref value);
    }

    public static int DaqDoSetBit(IntPtr handle, ushort ring, ushort slave, ushort channel, byte value)
    {
        return (int)Motion.mAcm_DaqDoSetBitEx(handle, ring, slave, channel, value);
    }
}
