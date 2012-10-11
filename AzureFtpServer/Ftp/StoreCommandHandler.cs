using AzureFtpServer.Ftp;
using AzureFtpServer.Ftp.FileSystem;
using AzureFtpServer.General;

namespace AzureFtpServer.FtpCommands
{
    internal class StoreCommandHandler : FtpCommandHandler
    {
        private const int m_nBufferSize = 65536;

        public StoreCommandHandler(FtpConnectionObject connectionObject)
            : base("STOR", connectionObject)
        {
        }

        protected override string OnProcess(string sMessage)
        {
            string sFile = GetPath(sMessage);
            string lower_sFile = sFile.ToLower();

            if (ConnectionObject.FileSystemObject.FileExists(lower_sFile))
            {
                // si el archivo existe lo eliminamos antes de crearlo!!!
                if (!ConnectionObject.FileSystemObject.Delete(lower_sFile))
                    return GetMessage(553, "File already exists. Y no se pudo eliminar (agregardo por ED)");
            }

            IFile file = ConnectionObject.FileSystemObject.OpenFile(sFile, true);

            var socketReply = new FtpReplySocket(ConnectionObject);

            if (!socketReply.Loaded)
            {
                return GetMessage(425, "Error in establishing data connection.");
            }

            var abData = new byte[m_nBufferSize];

            SocketHelpers.Send(ConnectionObject.Socket, GetMessage(150, "Opening connection for data transfer."));

            int nReceived = socketReply.Receive(abData);

            while (nReceived > 0)
            {
                file.Write(abData, nReceived);
                nReceived = socketReply.Receive(abData);
            }

            ConnectionObject.FileSystemObject.Put(lower_sFile, file);
            file.Close();

            socketReply.Close();

            return GetMessage(226, "Uploaded file successfully.");
        }
    }
}