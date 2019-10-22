﻿using MSDAD.Library;
using System;

namespace MSDAD
{
    namespace Server
    {
        class RemoteServer : MarshalByRefObject, ServerInterface
        {
            delegate void InvokeDelegate(string message);

            Communication communication;
            public RemoteServer(Communication communication)
            {
                this.communication = communication;
            }
            public void Close(string meeting_topic)
            {
                throw new NotImplementedException();
            }

            public void Create(string meeting_topic)
            {
                throw new NotImplementedException();
            }

            public void Join(string meeting_topic)
            {
                throw new NotImplementedException();
            }

            public string List()
            {
                throw new NotImplementedException();
            }

            public void Ping(int port, string message)
            {
                Console.WriteLine("Received message: " + message);
                Console.WriteLine("Will broadcast it to all available clients... ");
                communication.BroadcastPing(port, message);
                Console.Write("Success!");
            }

            public void Wait(int milliseconds)
            {
                throw new NotImplementedException();
            }

            public void Hello(int port)
            {
                communication.AddPortArray(port);
            }
        }
    }
    
}
