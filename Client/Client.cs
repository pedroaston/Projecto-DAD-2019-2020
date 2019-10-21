﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Text;
using System.Threading.Tasks;

namespace MSDAD
{
    class Client
    {
        static void Main(string[] args)
        {
            String port = Console.ReadLine();
            TcpChannel channel = new TcpChannel(Int32.Parse(port));
            ChannelServices.RegisterChannel(channel, true);
            while (true)
            {
                Console.ReadLine();
            }
        }
    }

    public class ClientView
    {
        public int port;
    }
}