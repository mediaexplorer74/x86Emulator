// ** HDD ***
// ** Experimental (not used yet) **

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using x86Emulator.Configuration;

namespace x86Emulator.Devices
{
    public class HDD : IDevice, INeedsIRQ, INeedsDMA
    {
        private int IrqNumber = 14;
        private const int DmaChannel = 2; // 2 - secondary dma; 5 -- ultra dma 
        private readonly int[] portsUsed = { 0x1f0, 0x1f1, 0x1f2, 0x1f3, 0x1f4, 0x1f5, 0x1f6, 0x1f7,
                                               0x170, 0x171, 0x172, 0x173, 0x174, 0x175, 0x176, 0x177,
                                               0x3f6, 0x376 }; // ?

        private readonly byte[][] data = new byte[2][];

        private Stream[] hddStream = new Stream[2];
        private BinaryReader[] hddReader = new BinaryReader[2];
        private IRandomAccessStream[] fileopen = new IRandomAccessStream[2];
        private bool primarySelected = true;

        private DORSetting[] digitalOutput = new DORSetting[2];
        private MainStatus[] mainStatus = new MainStatus[2];
        private bool[] inCommand = new bool[2];
        private byte[] paramCount = new byte[2];
        private byte[] resultCount = new byte[2];
        private byte[] paramIdx = new byte[2];
        private byte[] resultIdx = new byte[2];
        private HDDCommand[] command = new HDDCommand[2];
        private byte[] statusZero = new byte[2];
        private byte[] headPosition = new byte[2];
        private byte[] currentCyl = new byte[2];
        private bool[] interruptInProgress = new bool[2];

        public event EventHandler IRQ;
        public event EventHandler<ByteArrayEventArgs> DMA;

        public int[] PortsUsed
        {
            get { return portsUsed; }
        }

        public int IRQNumber
        {
            get { return IrqNumber; }
        }

        public int DMAChannel
        {
            get { return DmaChannel; }
        }

        public HDD()
        {
            mainStatus[0] = MainStatus.RQM;
            mainStatus[1] = MainStatus.RQM;
            data[0] = new byte[16];
            data[1] = new byte[16];
        }

        public void OnDMA(ByteArrayEventArgs e)
        {
            EventHandler<ByteArrayEventArgs> handler = DMA;
            if (handler != null)
                handler(this, e);
        }

        public void OnIRQ(EventArgs e)
        {
            EventHandler handler = IRQ;
            if (handler != null)
                handler(this, e);
        }

        private int GetHDDIndex(HDDType type)
        {
            int index = 0;
            if (type != HDDType.PrimaryHDD)
            {
                index = 1;
            }
            return index;
        }

        private int GetHDDIndex()
        {
            int index = 0;
            if (!primarySelected)
            {
                index = 1;
            }
            return index;
        }
        public void UnMountImage(HDDType type)
        {
            try
            {
                int index = GetHDDIndex(type);

                if (hddStream[index] != null)
                {
                    hddStream[index].Dispose();
                    fileopen[index].Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        public async Task<bool> MountImage(StorageFile hdd, HDDType type)
        {
            if (hdd == null)
                return false;

            UnMountImage(type);

            int index = GetHDDIndex(type);

            try
            {
                fileopen[index] = await hdd.OpenAsync(FileAccessMode.ReadWrite);
                hddStream[index] = fileopen[index].AsStream();
                hddReader[index] = new BinaryReader(hddStream[index]);
            }catch(Exception ex)
            {
                Helpers.Logger(ex);
                return false;
            }

            return true;
        }

        private void Reset()
        {
            Helpers.Logger("Reset issued");
            int index = GetHDDIndex();
            digitalOutput[index] &= ~DORSetting.Reset;
            OnIRQ(new EventArgs());
        }

        private void ReadSector()
        {
            SystemConfig.IO_HDDCall();
            int index = GetHDDIndex();
            int addr = (data[index][1] * 2 + data[index][2]) * 18 + (data[index][3] - 1);
            int numSectors = data[index][5] - data[index][3] + 1;

            if (numSectors == -1)
                numSectors = 1;

            if (hddStream[index] != null)
            {
                hddStream[index].Seek(addr * 512, SeekOrigin.Begin);
                byte[] sector = hddReader[index].ReadBytes(512 * numSectors);

                if (Helpers.DebugFile)
                {
                    Helpers.Logger(String.Format("Reading {0} sectors from sector offset {1}", numSectors, addr));
                    Debug.WriteLine("[i] " + String.Format("Reading {0} sectors from sector offset {1}", numSectors, addr));
                }

                resultCount[index] = 7;
                resultIdx[index] = 0;
                data[index][0] = 0;
                data[index][1] = 0;
                data[index][2] = 0;
                data[index][3] = 0;
                data[index][4] = 0;
                data[index][5] = 0;
                data[index][6] = 0;

                OnDMA(new ByteArrayEventArgs(sector));
            }
            mainStatus[index] |= MainStatus.DIO;
            statusZero[index] = 0;

            OnIRQ(new EventArgs());
        }
        private void Seek()
        {
            SystemConfig.IO_HDDCall();
            int index = GetHDDIndex();
            int addr = (data[index][1] * 2 + data[index][2]) * 18 + (data[index][3] - 1);
            int numSectors = data[index][5] - data[index][3] + 1;
            if (hddStream[index] != null)
            {
                hddStream[index].Seek(addr * 512, SeekOrigin.Begin);

                if (Helpers.DebugFile)
                    Helpers.Logger(String.Format("Seek {0} sectors from sector offset {1}", numSectors, addr));

                resultCount[index] = 7;
                resultIdx[index] = 0;
                data[index][0] = 0;
                data[index][1] = 0;
                data[index][2] = 0;
                data[index][3] = 0;
                data[index][4] = 0;
                data[index][5] = 0;
                data[index][6] = 0;
            }
            mainStatus[index] |= MainStatus.DIO;
            statusZero[index] = 0;

            OnIRQ(new EventArgs());
        }

        private void RunCommand()
        {
            int index = GetHDDIndex();
            switch (command[index])
            {
                case HDDCommand.Recalibrate:
                    Helpers.Logger("Recalibrate issued");
                   
                    if (hddReader[index] != null)
                    {
                        hddStream[index].Seek(0, SeekOrigin.Begin);
                    }
                    headPosition[index] = 0;
                    currentCyl[index] = 0;
                    statusZero[index] = 0x20;
                    interruptInProgress[index] = true;
                    OnIRQ(new EventArgs());
                    break;
                case HDDCommand.SenseInterrupt:
                    Helpers.Logger("Sense interrupt isssued");
                    if (!interruptInProgress[index])
                        statusZero[index] = 0x80;
                    interruptInProgress[index] = false;
                    mainStatus[index] |= MainStatus.DIO;
                    resultIdx[index] = 0;
                    resultCount[index] = 2;
                    data[index][0] = statusZero[index];
                    data[index][1] = currentCyl[index];
                    break;
                case HDDCommand.Seek:
                    Seek();
                    break;
                case HDDCommand.ReadData:
                    ReadSector();
                    break;
                case HDDCommand.WriteData:
                    resultCount[index] = 7;
                    resultIdx[index] = 0;
                    data[index][0] = 0;
                    data[index][1] = 0x2;
                    data[index][2] = 0;
                    data[index][3] = 0;
                    data[index][4] = 0;
                    data[index][5] = 0;
                    data[index][6] = 0;

                    mainStatus[index] |= MainStatus.DIO;
                    statusZero[index] = 0;

                    OnIRQ(new EventArgs());
                    break;

                default:
                    System.Diagnostics.Debugger.Break();
                    break;
            }
        }

        private void ProcessCommandAndArgs(ushort value)
        {
            int index = GetHDDIndex();
            if (inCommand[index])
            {
                data[index][paramIdx[index]++] = (byte)value;
                if (paramIdx[index] == paramCount[index])
                {
                    RunCommand();
                    inCommand[index] = false;
                }
            }
            else
            {
                inCommand[index] = true;
                paramIdx[index] = 0;
                command[index] = (HDDCommand)(value & 0x0f);
                switch (command[index])
                {
                    case HDDCommand.Recalibrate:
                        paramCount[index] = 1;
                        break;
                    case HDDCommand.SenseInterrupt:
                        paramCount[index] = 0;
                        RunCommand();
                        inCommand[index] = false;
                        break;
                    case HDDCommand.Seek:
                        paramCount[index] = 8;
                        break;
                    case HDDCommand.ReadData:
                        paramCount[index] = 8;
                        break;
                    case HDDCommand.WriteData:
                        paramCount[index] = 8;
                        break;
                    default:
                        System.Diagnostics.Debugger.Break();
                        break;
                }
            }
        }

        #region IDevice Members

        public uint Read(ushort addr, int size)
        {
            SystemConfig.IO_HDDCall();
            int index = GetHDDIndex();
            switch (addr)
            {
                case 0x3f2:
                    return (ushort)digitalOutput[index];
                case 0x3f4:
                    return (ushort)mainStatus[index];
                case 0x3f5:
                    if (hddReader[index] != null)
                    {
                        byte ret = data[index][resultIdx[index]++];
                        if (resultIdx[index] == resultCount[index])
                            mainStatus[index] &= ~MainStatus.DIO;
                        return ret;
                    }
                    break;
                default:
                    System.Diagnostics.Debugger.Break();
                    break;
            }
            return 0;
        }

        public void Write(ushort addr, uint value, int size)
        {
            SystemConfig.IO_HDDCall();
            int index = GetHDDIndex();

            switch (addr)
            {
                case 0x3f2:
                    if (((digitalOutput[index] & DORSetting.Reset) == 0) 
                        && (((DORSetting)value & DORSetting.Reset) == DORSetting.Reset))
                        Reset();

                    digitalOutput[index] = (DORSetting)value;
                    break;
                case 0x3f5:
                    ProcessCommandAndArgs((ushort)value);
                    break;
                default:
                    System.Diagnostics.Debugger.Break();
                    break;
            }
        }

        #endregion
    }

    [Flags]
    enum MainStatus1
    {
        Drive0Busy = 0x1,
        Drive1Busy = 0x2,
        Drive2Busy = 0x4,
        Drive3Busy = 0x8,
        CommandBusy = 0x10,
        NonDMA = 0x20,
        DIO = 0x40,
        RQM = 0x80
    }

    [Flags]
    enum DORSetting1
    {
        Drive = 0x1,
        Reset = 0x4,
        Dma = 0x8,
        Drive0Motor = 0x10,
        Drive1Motor = 0x20,
        Drive2Motor = 0x40,
        Drive3Motor = 0x80
    }

    enum HDDCommand
    {
        ReadTrack = 2,
        SPECIFY = 3,
        SenseDriveStatus = 4,
        WriteData = 5,
        ReadData = 6,
        Recalibrate = 7,
        SenseInterrupt = 8,
        WriteDeletedData = 9,
        ReadID = 10,
        ReadDeletedData = 12,
        FormatTrack = 13,
        Seek = 15,
        Version = 16,
        ScanEqual = 17,
        PerpendicularMode = 18,
        Configure = 19,
        Lock = 20,
        Verify = 22,
        ScanLowOrEqual = 25,
        ScanHighOrEqual = 29
    };

    public enum HDDType
    {
        PrimaryHDD,
        SecondaryHDD
    }
}