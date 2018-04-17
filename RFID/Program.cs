using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using ReaderB;
using Opc.UaFx;
using Opc.UaFx.Client;

namespace RFID
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("Press enter to close");
            var program = new RunProgram();

            program.Run();

            Console.ReadLine();

            Console.WriteLine("Closing Program, Please wait");
            program.Dispose();
        }
    }

    internal class RunProgram : IDisposable
    {
        #region Class Variables

        //static List<Thread> readerThreads = new List<Thread>();
        private static readonly List<RFID_Reader> readers = new List<RFID_Reader>();

        private static readonly List<OpcSubscription> opcSubscriptions = new List<OpcSubscription>();
        private static readonly OpcClient _client = new OpcClient();
        private List<string> EPC_Items = new List<string>();
        private static int fCmdRet;

        #endregion Class Variables

        internal void Run()
        {
            var failure = 0;
            var totalFailure = 0;

            #region Setup Readers

            for (int i = 8; i <= 9; i++)
            {
                readers.Add(
                    new RFID_Reader()
                    {
                        PortHandle = 6000,
                        IP_Address = IPAddress.Parse("172.16." + i + ".1")
                    });

                //Try to close any current open ports that would prevent reader from connecting
                //Dont handle failure of closure, assume already closed
                readers[i - 8].CloseConnection();

                var success = readers[i - 8].OpenConnection();
                if (success == -1)
                {
                    Console.WriteLine("Error connecting to RFID Reader on Station " + i);
                    failure++;
                    if (failure < 6) //5 Retries maximum
                    {
                        Console.WriteLine("Retrying: Try {0}", failure);
                        readers.RemoveAt(i - 8);
                        i--;
                        Thread.Sleep(500);
                    }
                    else
                    {
                        totalFailure++;
                    }
                }
                else
                {
                    Console.WriteLine("Sucecssfully opened connection to reader on Station {0} on IP Address: {1}\n", i, readers[readers.Count() - 1].IP_Address);
                    failure = 0;
                }

                if (totalFailure > 6)
                {
                    Console.WriteLine("Unable to connect to RFID reader successfully. Aborting");
                    break;
                }
            }
            if (totalFailure > 6)
            {
                return;
            }

            #endregion Setup Readers

            #region Setup OPC UA

            _client.ServerAddress = new Uri("opc.tcp://127.0.0.1:49320");
            _client.ApplicationName = AppDomain.CurrentDomain.FriendlyName;
            _client.Security.AutoAcceptUntrustedCertificates = true;
            _client.Connect();

            for (int i = 8; i <= 13; i++)
            {
                var readNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_" + i + "_Read");
                opcSubscriptions.Add(_client.SubscribeDataChange(readNodeId, ReadTag_ValueChanged));
                Console.WriteLine("Subscribed to node {0}\nCurrent Node Value {1}\n", readNodeId.Value, _client.ReadNode(readNodeId).Value);
            }

            var writeNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_8_Write");
            opcSubscriptions.Add(_client.SubscribeDataChange(writeNodeId, WriteTag_ValueChanged));

            writeNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_9_Write");
            opcSubscriptions.Add(_client.SubscribeDataChange(writeNodeId, WriteTag_ValueChanged));

            #endregion Setup OPC UA
        }

        #region Monitor Read Tag

        /// <summary>
        /// Read Tag Value when OPC triggered to true
        /// </summary>
        /// <param name="sender">Of type <see cref="OpcMonitoredItem"/></param>
        /// <param name="e">     Not Implimented</param>
        private void ReadTag_ValueChanged(object sender, OpcDataChangeEventArgs e)
        {
            var monitoredNode = (OpcMonitoredItem)sender;
            var nodeValue = (bool)_client.ReadNode(monitoredNode.NodeId).Value;
            int stationNum = int.Parse(monitoredNode.NodeId.ToString()
                .Remove(0, 42).Substring(
                monitoredNode.NodeId.ToString()
                .Remove(0, 42).IndexOf("_", 0) + 1, 1));
            Console.WriteLine("Node {0} Value Changed: {1}", monitoredNode.NodeId, nodeValue);

            if (nodeValue) //True
            {
                var EPC_ID = Inventory(stationNum);

                if (EPC_ID == null)
                {
                    Console.WriteLine("No Tags Found at Station {0}", stationNum);
                    return;
                }
                else
                {
                    Console.WriteLine("Tag ID at Station {0}:{1}", stationNum, EPC_ID[0]);
                }
                Thread.Sleep(500);
                var EPC_Val = ReadEPC(stationNum, EPC_ID);
                Console.WriteLine("Tag ID at Station {0}:{1}", stationNum, EPC_Val);
            }
        }

        #endregion Monitor Read Tag

        #region Monitor Write Tag

        private void WriteTag_ValueChanged(object sender, OpcDataChangeEventArgs e)
        {
            var monitoredNode = (OpcMonitoredItem)sender;
            var nodeValue = (bool)_client.ReadNode(monitoredNode.NodeId).Value;
            int stationNum = int.Parse(monitoredNode.NodeId.ToString()
                .Remove(0, 42).Substring(
                monitoredNode.NodeId.ToString()
                .Remove(0, 42).IndexOf("_", 0) + 1, 1));

            var fPassword = RfidConversion.HexStringToByteArray("00000000");
            var reader = readers[stationNum - 8];

            var guid = Guid.NewGuid().ToString().Replace("-", null).ToUpper(); //32 characters long

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

            #endregion ReaderRequirements

            if (nodeValue) //True - Write New Value
            {
                var EPC_ID = Inventory(stationNum);
                var EPC_Val = ReadEPC(stationNum, EPC_ID);
                Console.WriteLine(EPC_Val);

                #region Setup GUID

                guid = guid + EPC_Val.Substring(EPC_Val.Length - 4);
                ENum = Convert.ToByte(EPC_ID[0].Length / 4);
                EPClength = Convert.ToByte(ENum * 2);
                byte[] EPC = new byte[ENum];

                WNum = Convert.ToByte(guid.Length / 4);
                byte[] Writedata = new byte[WNum * 2];

                Writedata = RfidConversion.HexStringToByteArray(guid);
                Writedatalen = Convert.ToByte(WNum * 2);

                #endregion Setup GUID

                fCmdRet = StaticClassReaderB.WriteCard_G2(ref reader.ComAddrr, EPC, Mem, WordPtr, Writedatalen, Writedata, fPassword, MaskAdd, MaskLen, MaskFlag, WrittenDataNum, EPClength, ref reader.ErrorCode, reader.ComAddrr);

                #region Write to OPC UA

                var tagNodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_" + stationNum + "_Tag");

                _client.WriteNode(tagNodeId, guid);

                #endregion Write to OPC UA
            }
        }

        #endregion Monitor Write Tag

        #region Get Tags

        /// <summary>
        /// <para>Gets current tags within range and return the first tag. Inventory not kept.</para>
        /// <remarks>
        /// <para>
        /// Returns EPC of type <see cref="string"/>[2] || EPC[0] = Tag Lenght | EPC[1] = Tag ID
        /// </para>
        /// </remarks>
        /// </summary>
        /// <param name="stationNum">Station Number (8-13)</param>
        /// <returns>[0] = Tag Lenght [1] = Tag ID</returns>
        private string[] Inventory(int stationNum)
        {
            var reader = readers[stationNum - 8];

            #region ReaderInventoryReq

            byte AdrTID = 0;
            byte LenTID = 0;
            byte TIDFlag = 0;

            byte[] EPC = new byte[5000];

            int Totallen = 0;
            int CardNum = 0;

            string[] fInventory_EPC_List = new string[2];

            #endregion ReaderInventoryReq

            fCmdRet = StaticClassReaderB.Inventory_G2(ref reader.ComAddrr, AdrTID, LenTID, TIDFlag, EPC, ref Totallen, ref CardNum, reader.PortHandle);

            if ((fCmdRet == 1) | (fCmdRet == 2) | (fCmdRet == 3) | (fCmdRet == 4)) //251 = no tags detected
            {
                byte[] daw = new byte[Totallen];
                Array.Copy(EPC, daw, Totallen);
                fInventory_EPC_List[0] = RfidConversion.ByteArrayToHexString(daw).Remove(0, 2);
                fInventory_EPC_List[1] = RfidConversion.ByteArrayToHexString(daw).Remove(2);
                return fInventory_EPC_List;
            }
            else
            {
                Console.WriteLine(RfidConversion.GetErrorCodeDesc(fCmdRet));
                return null;
            }
        }

        #endregion Get Tags

        #region Read EPC

        /// <summary>
        /// Read EPC Value given TagId
        /// </summary>
        /// <param name="stationNum">Station that Tag should be read at</param>
        /// <param name="str">       TagId</param>
        /// <returns></returns>
        private string ReadEPC(int stationNum, string[] str)
        {
            var reader = readers[stationNum - 8];

            #region ReaderRequirements

            byte WordPtr = 0;
            byte ENum = Convert.ToByte(str[0].Length / 4);
            byte Num = Convert.ToByte(10);
            byte Mem = 1;
            byte EPClength = Convert.ToByte(str[0].Length / 2);
            byte MaskFlag = 0, MaskAdd = 0, MaskLen = 0;

            byte[] EPC = new byte[ENum];
            byte[] CardData = new byte[320];
            byte[] fPassWord = RfidConversion.HexStringToByteArray("00000000");

            EPC = RfidConversion.HexStringToByteArray(str[0]);

            #endregion ReaderRequirements

            /* Mem = 1
             * WordPtr = 0
             * Num = 10
             * fPassWord = [0][0][0][0]
             * Maskadr = 0
             * MaskLen = 0
             * MaskFlag = 0
             * CardData[320] = 0
             * EPClenght = 12
             * ferrorcode = -1
             *
            */

            fCmdRet = StaticClassReaderB.ReadCard_G2(ref reader.ComAddrr, EPC, Mem, WordPtr, Num, fPassWord, MaskAdd, MaskLen, MaskFlag, CardData, EPClength, ref reader.ErrorCode, reader.PortHandle);

            if (fCmdRet == 0) //Successful read
            {
                byte[] daw = new byte[Num * 2];
                Array.Copy(CardData, daw, Num * 2);
                var nodeId = new OpcNodeId("2:RFID_Communications.RFID Readers.Station_" + stationNum + "_Tag");
                _client.WriteNode(nodeId, RfidConversion.ByteArrayToHexString(daw));
                return RfidConversion.ByteArrayToHexString(daw);
            }
            else if (reader.ErrorCode != -1)
            {
                Console.WriteLine("Error reading EPC Value. ErrorCode=0x{0}({1})", Convert.ToString(reader.ErrorCode, 2), RfidConversion.GetErrorCodeDesc(reader.ErrorCode));
                return null;
            }
            else
            {
                return null;
            }
        }

        #endregion Read EPC

        #region Close Application -- Dispose

        public void Dispose()
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
            _client.Dispose();
        }

        #endregion Close Application -- Dispose
    }
}