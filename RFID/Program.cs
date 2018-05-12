using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Threading;
using ReaderB;
using Opc.UaFx;
using Opc.UaFx.Client;
using System.ComponentModel;
using System.Threading.Tasks;

namespace RFID
{
    internal class Program : IDisposable
    {
        private static readonly List<Thread> Threads = new List<Thread>();
        public static readonly List<RunRfid> RfidReaders = new List<RunRfid>();

        private static void Main(string[] args)
        {
            Console.WriteLine("Press enter to close");

            Thread.CurrentThread.Name = "main";

            #region Setup RFID Readers

            Console.WriteLine("Setting Up RFID Threads");
            for (int i = 8; i < 13; i++)
            {
                Console.WriteLine(i);
                Thread t = new Thread(new ThreadStart(delegate
                {
                    RfidReaders.Add(new RunRfid(i));
                }))
                {
                    Name = "Reader #" + i
                };
                Console.WriteLine("Thread Configured for Station {0}", i);
                Console.WriteLine("Thread {0} started -  {1}", t.Name, t.IsAlive);
                Threads.Add(t);
                Threads[Threads.Count - 1].Start();
                Console.WriteLine("Thread {0} started? {1}", t.Name, t.IsAlive);
                Thread.Sleep(1000);
            }

            var opcClient = new RunOpc();
            

            #endregion Setup RFID Readers

            Console.ReadLine();

            Console.WriteLine("Closing Program, Please wait");
        }

        public void Dispose()
        {
            foreach (var thread in Threads)
            {
                thread.Join();
            }

            foreach (var runProgram in RfidReaders)
            {
                runProgram.Dispose();
            }
        }
    }

    internal class RunOpc : IDisposable
    {
        private static readonly List<OpcSubscription> OpcSubscriptions = new List<OpcSubscription>();
        private static readonly OpcClient Client = new OpcClient();

        #region Constructor

        /// <summary>
        /// Setup Opc Client
        /// </summary>
        public RunOpc()
        {
            #region Setup OPC UA

            Client.ServerAddress = new Uri("opc.tcp://127.0.0.1:49320");
            Client.ApplicationName = AppDomain.CurrentDomain.FriendlyName;
            Client.Security.AutoAcceptUntrustedCertificates = true;
            Client.Connect();

            for (int i = 8; i <= 13; i++)
            {
                var readNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_" + i + "_Read");
                OpcSubscriptions.Add(Client.SubscribeDataChange(readNodeId, ReadTag_ValueChanged));
                Console.WriteLine("Subscribed to node {0}\nCurrent Node Value {1}\n", readNodeId.Value, Client.ReadNode(readNodeId).Value);
            }

            var writeNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_8_Write");
            OpcSubscriptions.Add(Client.SubscribeDataChange(writeNodeId, WriteTag_ValueChanged));

            writeNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_9_Write");
            OpcSubscriptions.Add(Client.SubscribeDataChange(writeNodeId, WriteTag_ValueChanged));

            #endregion Setup OPC UA
        }

        #endregion Constructor

        #region Monitor Read Tag

        /// <summary>
        /// Read Tag Value when OPC triggered to true
        /// </summary>
        /// <param name="sender">Of type <see cref="OpcMonitoredItem"/></param>
        /// <param name="e">     Not Implimented</param>
        private static void ReadTag_ValueChanged(object sender, OpcDataChangeEventArgs e)
        {
            var monitoredNode = (OpcMonitoredItem)sender;
            var nodeValue = (bool)Client.ReadNode(monitoredNode.NodeId).Value;
            int stationNum = int.Parse(monitoredNode.NodeId.ToString()
                .Remove(0, 42).Substring(
                    monitoredNode.NodeId.ToString()
                        .Remove(0, 42).IndexOf("_", 0, StringComparison.Ordinal) + 1, 1));

            Console.WriteLine("Node {0} Value Changed: {1}", monitoredNode.NodeId, nodeValue);

            if (!nodeValue || Program.RfidReaders.Count == 0) return;
            var reader = Program.RfidReaders.FirstOrDefault(i => i.ReaderName == ("Station" + stationNum));
            if (reader == null) return;
            var epcId = reader.ReadEpc();

            if(epcId == null)
            {
                Client.WriteNode("2:RFID_Communications.RFID Readers.Station_" + stationNum + "_Tag", "");
            }
            else
            {
                Client.WriteNode("2:RFID_Communications.RFID Readers.Station_" + stationNum + "_Tag", epcId);
            }
            
            Client.WriteNode(monitoredNode.NodeId, false);
        }

        #endregion Monitor Read Tag

        #region Monitor Write Tag

        private void WriteTag_ValueChanged(object sender, OpcDataChangeEventArgs e)
        {
            var monitoredNode = (OpcMonitoredItem)sender;
            var nodeValue = (bool)Client.ReadNode(monitoredNode.NodeId).Value;
            int stationNum = int.Parse(monitoredNode.NodeId.ToString()
                .Remove(0, 42).Substring(
                monitoredNode.NodeId.ToString()
                .Remove(0, 42).IndexOf("_", 0, StringComparison.Ordinal) + 1, 1));

            if (!nodeValue) // False
            {
                return;
            }

            #region Write to OPC UA

            var tagNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_" + stationNum + "_Tag");
            var reader = Program.RfidReaders.FirstOrDefault(i => i.ReaderName == "Station" + stationNum);
            if (reader == null) return;
            var guid = Program.RfidReaders.First(i => i.ReaderName == "Station" + stationNum).WriteEpc();

            Client.WriteNode(monitoredNode.NodeId, false);
            Client.WriteNode(tagNodeId, guid);

            #endregion Write to OPC UA
        }

        #endregion Monitor Write Tag

        public void Dispose()
        {
            Client.Disconnect();
        }
    }

    internal class RunRfid : IDisposable, INotifyPropertyChanged
    {
        internal RFID_Reader Reader;
        private static int _fCmdRet;
        private bool _readerOpen;
        private readonly object _locker = new object();

        public event PropertyChangedEventHandler PropertyChanged;

        public bool ReaderOpen
        {
            get { return _readerOpen; }
            set
            {
                _readerOpen = value;
                OnPropertyChanged("ReaderOpen");
            }
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;

            if(handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }

        public string ReaderName { get; set; }

        #region Constructor

        /// <summary>
        /// Constructor of RFID Readers
        /// </summary>
        /// <param name="readerNumber">Provides a Station Number</param>
        public RunRfid(int readerNumber)
        {
            lock(_locker)
            {
                #region Setup RFID Reader
                Console.WriteLine(readerNumber);

                Reader = new RFID_Reader()
                {
                    PortHandle = 6000,
                    IP_Address = IPAddress.Parse("172.16." + readerNumber + ".1"),
                };
                ReaderName = "Station" + readerNumber;
                Console.Write("RFID Information:\nPort:{0}\nIP Address:{1}\nName:{2}", Reader.Port, Reader.IP_Address, ReaderName);

                //Try to close any current open ports that would prevent reader from connecting
                //Dont handle failure of closure, assume already closed
                Reader.CloseConnection();

                for (int i = 1; i < 6; i++)
                {
                    Console.WriteLine(Thread.CurrentThread.Name);
                    var success = Reader.OpenConnection();

                    if (success != -1)
                    {
                        //Allow for waiting on the 
                        ReaderOpen = true;

                        //Notify Successful connection
                        Console.WriteLine("Sucecssfully opened connection to reader on Station {0} on IP Address: {1}\n", i, Reader.IP_Address);

                        //Exits Loop if successfully connected
                        break;
                    }
                    Console.WriteLine("Error connecting to RFID Reader on {0}", ReaderName);
                    Console.WriteLine("Retrying: Try {0}", i);
                    Thread.Sleep(500);
                }

                #endregion Setup RFID Reader
            }

        }

        #endregion Constructor


        #region Get Tags

        /// <summary>
        /// <para>Gets current tags within range and return the first tag. Inventory not kept.</para>
        /// <remarks>
        /// <para>
        /// Returns EPC of type <see cref="string"/>[2] || EPC[0] = Tag Lenght | EPC[1] = Tag ID
        /// </para>
        /// </remarks>
        /// </summary>
        /// <returns>[0] = Tag Lenght [1] = Tag ID</returns>
        private string[] Inventory()
        {
            #region ReaderInventoryReq

            byte AdrTID = 0;
            byte LenTID = 0;
            byte TIDFlag = 0;

            byte[] EPC = new byte[5000];

            int Totallen = 0;
            int CardNum = 0;

            string[] fInventory_EPC_List = new string[2];

            #endregion ReaderInventoryReq

            _fCmdRet = StaticClassReaderB.Inventory_G2(ref Reader.ComAddrr, AdrTID, LenTID,
                TIDFlag, EPC, ref Totallen, ref CardNum, Reader.PortHandle);

            if ((_fCmdRet == 1) | (_fCmdRet == 2) | (_fCmdRet == 3) | (_fCmdRet == 4)) //251 = no tags detected
            {
                byte[] daw = new byte[Totallen];
                Array.Copy(EPC, daw, Totallen);
                fInventory_EPC_List[0] = RfidConversion.ByteArrayToHexString(daw).Remove(0, 2);
                fInventory_EPC_List[1] = RfidConversion.ByteArrayToHexString(daw).Remove(2);
                return fInventory_EPC_List;
            }

            Console.WriteLine(RfidConversion.GetErrorCodeDesc(_fCmdRet));
            return null;
        }

        #endregion Get Tags

        #region Public Read EPC

        /// <summary>
        /// Read EPC Value WITH Lock for ThreadSafe
        /// </summary>
        /// <returns></returns>
        public string ReadEpc()
        {
            lock (_locker)
            {
                return Read_Epc();
            }
        }

        #endregion Public Read EPC

        #region Private Read EPC

        /// <summary>
        /// Used to read an EPC with NO Lock on the system
        /// </summary>
        /// <returns></returns>
        private string Read_Epc()
        {
            var str = Inventory();
            if (str == null)
            {
                Console.WriteLine("No Tags Found at {0}", ReaderName);
                return null;
            }

            Console.WriteLine("Tag ID at {0}:{1}", ReaderName, str[0]);
            Thread.Sleep(500);

            #region ReaderRequirements

            const byte wordPtr = 0;
            const byte mem = 1;
            byte[] cardData = new byte[320];

            byte wNum = Convert.ToByte(Convert.ToInt64(str[1]) - 2);
            byte epcLength = Convert.ToByte(str[0].Length / 2);
            byte eNum = Convert.ToByte(str[0].Length / 4);

            byte MaskFlag = 0, MaskAdd = 0, MaskLen = 0;
            var fPassWord = RfidConversion.HexStringToByteArray("00000000");

            byte[] epc = new byte[eNum];
            epc = RfidConversion.HexStringToByteArray(str[0]);

            #endregion ReaderRequirements

            _fCmdRet = StaticClassReaderB.ReadCard_G2(ref Reader.ComAddrr, epc, mem, wordPtr, wNum, fPassWord, MaskAdd,
                MaskLen, MaskFlag, cardData, epcLength, ref Reader.ErrorCode, Reader.PortHandle);

            if (_fCmdRet == 0) //Successful read
            {
                byte[] daw = new byte[wNum * 2];
                Array.Copy(cardData, daw, wNum * 2);

                Console.WriteLine("Tag ID at {0}:{1}", ReaderName, RfidConversion.ByteArrayToHexString(daw));

                return RfidConversion.ByteArrayToHexString(daw);
            }

            if (Reader.ErrorCode == -1) return null;
            Console.WriteLine(
                "Error reading EPC Value. ErrorCode=0x{0}({1})",
                Convert.ToString(Reader.ErrorCode, 2),
                RfidConversion.GetErrorCodeDesc(Reader.ErrorCode));
            return null;
        }

        #endregion Private Read EPC

        #region Write EPC

        public string WriteEpc()
        {
            lock (_locker)
            {
                #region ReaderRequirements

                byte WordPtr = 1, ENum;
                byte Mem = 1;
                byte WNum = 0;
                byte EPClength = 0;
                byte Writedatalen = 0;
                int WrittenDataNum = 0;
                byte[] CardData = new byte[320];
                byte[] writedata = new byte[230];

                byte MaskFlag = 0, MaskAdd = 0, MaskLen = 0;

                var fPassword = RfidConversion.HexStringToByteArray("00000000");

                #endregion ReaderRequirements

                var epcVal = Read_Epc();

                #region Setup GUID

                var guid = Guid.NewGuid().ToString().Replace("-", null).ToUpper(); //32 characters long
                guid = guid + epcVal.Substring(epcVal.Length - 4);
                ENum = Convert.ToByte(epcVal.Length / 4);
                EPClength = Convert.ToByte(ENum * 2);
                byte[] EPC = new byte[ENum];

                WNum = Convert.ToByte(guid.Length / 4);
                byte[] Writedata = new byte[WNum * 2];

                Writedata = RfidConversion.HexStringToByteArray(guid);
                Writedatalen = Convert.ToByte(WNum * 2);

                #endregion Setup GUID

                _fCmdRet = StaticClassReaderB.WriteCard_G2(ref Reader.ComAddrr, EPC, Mem, WordPtr,
                    Writedatalen, Writedata, fPassword, MaskAdd, MaskLen, MaskFlag, WrittenDataNum,
                    EPClength, ref Reader.ErrorCode, Reader.ComAddrr);

                return guid;
            }
        }

        #endregion Write EPC

        #region Close Application -- Dispose

        public void Dispose()
        {
            Reader.Dispose();
        }

        #endregion Close Application -- Dispose
    }
}