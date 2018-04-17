using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ReaderB;

namespace RFID
{
    class RFID_Reader : IDisposable
    {

        /// <summary>
        /// RFID Reader IP Address
        /// </summary>
        public IPAddress IP_Address { get; set; }
        /// <summary>
        /// Port of the RFID Reader
        /// </summary>
        public int Port { get; set; } = 6000; //All thin clients post to port 6000

        public int ErrorCode;

        public byte ComAddrr = 0xff;
        /// <summary>
        /// Information on current Port Status
        /// </summary>
        public int PortHandle;

        public event EventHandler Disposed;

        public void Dispose()
        {
            StaticClassReaderB.CloseNetPort(PortHandle);
        }

        /// <summary>
        /// Creates the connection to the RFID Reader
        /// </summary>
        /// <returns>PortHandle -- Success or failure of the connection</returns>
        public int OpenConnection()
        {
            StaticClassReaderB.OpenNetPort(Port, IP_Address.ToString(), ref ComAddrr, ref PortHandle);

            return PortHandle;
        }

        public bool CloseConnection()
        {

            bool success = true;
            try
            {
                StaticClassReaderB.CloseNetPort(PortHandle);
            }
            catch
            {
                success = false;
            }

            return success;
            
        }
    }
}
