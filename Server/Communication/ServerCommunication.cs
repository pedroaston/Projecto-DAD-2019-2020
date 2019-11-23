﻿using MSDAD.Library;
using MSDAD.Server.XML;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace MSDAD.Server.Communication
{
    class ServerCommunication
    {
        int server_port, tolerated_faults, min_delay, max_delay, n_replicas;
        string server_ip, server_url, server_identifier, server_remoting;

        ServerLibrary server_library;
        RemoteServer remote_server;
        TcpChannel channel;


        // recebe as mensagens para cada meeting_topic
        private ConcurrentDictionary<string, List<string>> receiving_create = new ConcurrentDictionary<string, List<string>>(); // key: topic ; value: mensagens das replicas
        // topicos a criar que estao pendentes
        private List<string> pending_create = new List<string>();

        private Dictionary<string, string> client_addresses = new Dictionary<string, string>(); //key = client_identifier; value = client_address
        private Dictionary<string, string> server_addresses = new Dictionary<string, string>(); //key = server_identifier; value = server_address        

        public delegate void CreateAsyncDelegate(string meeting_topic, int min_attendees, List<string> slots, List<string> invitees, string client_identifier, string server_identifier);

        public static void OurRemoteAsyncCallBack(IAsyncResult ar)
        {
            CreateAsyncDelegate del = (CreateAsyncDelegate)((AsyncResult)ar).AsyncDelegate;
            return;
        }

        public ServerCommunication(ServerLibrary server_library)
        {
            this.server_library = server_library;
            this.server_identifier = server_library.ServerIdentifier;
            this.server_port = server_library.ServerPort;
            this.server_ip = server_library.ServerIP;
            this.server_remoting = server_library.ServerRemoting;
            this.tolerated_faults = server_library.ToleratedFaults;
            this.min_delay = server_library.MinDelay;
            this.max_delay = server_library.MaxDelay;
        }

        public void Start()
        {
            channel = new TcpChannel(this.server_port);
            ChannelServices.RegisterChannel(channel, true);

            this.remote_server = new RemoteServer(this);
            RemotingServices.Marshal(this.remote_server, server_remoting, typeof(RemoteServer));

            LocationAndRoomInit();
            ServerURLInit();

            this.server_url = ServerUtils.AssembleRemotingURL(this.server_ip, this.server_port, this.server_remoting);
            n_replicas = (tolerated_faults * 2) + 1;
        }

        public void Create(string meeting_topic, int min_attendees, List<string> slots, List<string> invitees, string client_identifier, string replica_identifier)
        {
            if(!this.receiving_create.ContainsKey(meeting_topic))
            {
                List<string> received_messages = new List<string>();
                received_messages.Add(this.server_identifier);
                this.receiving_create.AddOrUpdate(meeting_topic, received_messages, (key, oldValue) => received_messages);
            }
            else
            {
                List<string> received_messages = this.receiving_create[meeting_topic];
                
                if(!received_messages.Contains(replica_identifier))
                {
                    received_messages.Add(replica_identifier);
                    this.receiving_create[meeting_topic] = received_messages;
                }                
            }

            lock (pending_create)
            {
                if (!pending_create.Contains(meeting_topic))
                {
                    pending_create.Add(meeting_topic);

                    int server_iter = 1;

                    foreach (string replica_url in this.server_addresses.Values)
                    {
                        Console.WriteLine("teste: " + server_iter);

                        if (server_iter == n_replicas)
                        {
                            break;
                        }

                        if (!replica_url.Equals(this.server_url))
                        {
                            ServerInterface remote_server = (ServerInterface)Activator.GetObject(typeof(ServerInterface), replica_url);
                            try
                            {
                                CreateAsyncDelegate RemoteDel = new CreateAsyncDelegate(remote_server.Create);
                                AsyncCallback RemoteCallback = new AsyncCallback(ServerCommunication.OurRemoteAsyncCallBack);
                                IAsyncResult RemAr = RemoteDel.BeginInvoke(meeting_topic, min_attendees, slots, invitees, client_identifier, this.server_identifier, RemoteCallback, null);
                            }
                            catch (System.Net.Sockets.SocketException se)
                            {
                                Console.WriteLine(se.Message);
                            }
                        }

                        server_iter++;
                    }

                    // TODO:  isto e bloqueante pode ficar bloqueado para sempre. Por Timer?
                    while (true)
                    {
                        float current_messages = (float)this.receiving_create[meeting_topic].Count;

                        Console.WriteLine(receiving_create[meeting_topic].Count);
                        Console.WriteLine((float)n_replicas / 2);

                        if (current_messages > (float)n_replicas / 2)
                        {
                            if (invitees == null)
                            {
                                foreach (KeyValuePair<string, string> address_iter in this.client_addresses)
                                {
                                    if (address_iter.Key != client_identifier)
                                    {
                                        ClientInterface client = (ClientInterface)Activator.GetObject(typeof(ClientInterface), "tcp://" + address_iter.Value);
                                        client.SendMeeting(meeting_topic, 1, "OPEN", null);
                                    }
                                }
                            }

                            Console.WriteLine("\r\nNew event: " + meeting_topic);
                            Console.Write("Please run a command to be run on the server: ");
                            break;
                        }
                    }                    
                }
            }
        }
        public void List(Dictionary<string, string> meeting_query, string client_identifier)
        {
            List<Meeting> event_list = this.server_library.GetEventList();

            ClientInterface remote_client = (ClientInterface)Activator.GetObject(typeof(ClientInterface), "tcp://" + this.client_addresses[client_identifier]);

            foreach (Meeting meeting in event_list)
            {
                if (!meeting_query.ContainsKey(meeting.Topic) && meeting.GetInvitees() == null)
                {
                    string state = meeting.State;
                    if (state.Equals("SCHEDULED") && meeting.ClientConfirmed(client_identifier))
                    {
                        string extraInfo = "Client Confirmed at " + meeting.FinalSlot;
                        remote_client.SendMeeting(meeting.Topic, meeting.Version, meeting.State, extraInfo);
                    }
                    else
                    {
                        remote_client.SendMeeting(meeting.Topic, meeting.Version, meeting.State, null);
                    }
                }
                else if (!meeting_query.ContainsKey(meeting.Topic) && meeting.GetInvitees() != null)
                {
                    if (meeting.GetInvitees().Contains(client_identifier)) {
                        string state = meeting.State;
                        if (state.Equals("SCHEDULED") && meeting.ClientConfirmed(client_identifier))
                        {
                            string extraInfo = "Client Confirmed at " + meeting.FinalSlot;
                            remote_client.SendMeeting(meeting.Topic, meeting.Version, meeting.State, extraInfo);
                        }
                        else
                        {
                            remote_client.SendMeeting(meeting.Topic, meeting.Version, meeting.State, null);
                        }
                    }
                }
                else if (meeting_query.ContainsKey(meeting.Topic) && !meeting.State.Equals(meeting_query[meeting.Topic]))
                {
                    string state = meeting.State;
                    if (state.Equals("SCHEDULED") && meeting.ClientConfirmed(client_identifier))
                    {
                        string extraInfo = "Client Confirmed at " + meeting.FinalSlot;
                        remote_client.SendMeeting(meeting.Topic, meeting.Version, meeting.State, extraInfo);
                    }
                    else
                    {
                        remote_client.SendMeeting(meeting.Topic, meeting.Version, meeting.State, null);
                    }
                }
            }
        }

        public void Join(string meeting_topic, List<string> slots, string client_identifier)
        {
            this.server_library.Join(meeting_topic, slots, client_identifier);
        }

        public void Close(String meeting_topic, string client_identifier)
        {
            this.server_library.Close(meeting_topic, client_identifier);
        }


        public void BroadcastPing(string message, string client_identifier)
        {
            foreach (KeyValuePair<string, string> address_iter in this.client_addresses)
            {
                if (address_iter.Key != client_identifier)
                {
                    ClientInterface client = (ClientInterface)Activator.GetObject(typeof(ClientInterface), "tcp://" + address_iter.Value);
                    client.Ping(message);
                }

            }
        }

        public Dictionary<string, string> GetClientAddresses()
        {
            return this.client_addresses;
        }

        public string GetClientAddress(string client_identifier)
        {
            return this.client_addresses[client_identifier];
        }

        public void AddClientAddress(string client_identifier, string client_remoting, string client_ip, int client_port)
        {
            string client_address;

            client_address = ServerUtils.AssembleAddress(client_ip, client_port);

            if (ServerUtils.ValidateAddress(client_address))
            {
                lock (this)
                {
                    try
                    {
                        client_addresses.Add(client_identifier, client_address + "/" + client_remoting);
                    }
                    catch (ArgumentException)
                    {
                        throw new ServerCoreException(ErrorCodes.USER_WITH_SAME_ID);
                    }
                }
            }
        }

        public void LocationAndRoomInit()
        {
            string directory_path, file_name;
            string[] directory_files;
            TextReader tr;
            Location location;
            LocationXML locationXML;


            directory_path = ServerUtils.AssembleCurrentPath() + "\\" + "Locations" + "\\";
            directory_files = Directory.GetFiles(directory_path);

            XmlSerializer xmlSerializer = new XmlSerializer(typeof(LocationXML));

            lock (this)
            {
                for (int i = 0; i < directory_files.Length; i++)
                {
                    file_name = directory_files[i];
                    tr = new StreamReader(file_name);

                    locationXML = (LocationXML)xmlSerializer.Deserialize(tr);

                    location = new Location(locationXML.Name);

                    foreach (RoomXML roomXML in locationXML.RoomViews)
                    {
                        location.Add(new Room(roomXML.Name, roomXML.Capacity));
                    }
                    tr.Close();
                    this.server_library.AddLocation(location);
                }
            }
        }

        public void ServerURLInit()
        {
            string server_id, server_url;

            for(int i = 1; i < 10; i++)
            {
                server_id = "s" + i;
                server_url = "tcp://localhost:300" + i + "/server" + i;
                this.server_addresses.Add(server_id, server_url);
            }

            for(int i = 10; i < 100; i++)
            {
                server_id = "s" + i;
                server_url = "tcp://localhost:30" + i + "/server" + i;
                this.server_addresses.Add(server_id, server_url);
            }
            
        }

        public void Status()
        {
            string client_identifier;
            string client_url;

            List<string> invitees;

            List<Location> locations;
            List<Meeting> meetings;
            List<Room> rooms;
            List<DateTime> reserved_dates;

            Dictionary<string, string> client_dictionary;

            locations = server_library.GetKnownLocations();
            client_dictionary = server_library.GetClients();
            meetings = server_library.GetEventList();

            foreach (Location location in locations)
            {
                Console.Write("Location: ");
                Console.WriteLine(location.Name);

                rooms = location.GetList();

                foreach (Room room in rooms)
                {
                    Console.WriteLine("  Room: " + room.Identifier);
                    Console.WriteLine("  Capacity: " + room.Capacity);
                    Console.WriteLine("  Dates Reserved: ");
                    reserved_dates = room.GetReservedDates();

                    foreach (DateTime dateTime in reserved_dates)
                    {
                        Console.WriteLine("    " + dateTime.ToString("yyyy-MMMM-dd"));
                    }
                }
            }


            foreach (KeyValuePair<string, string> client_pair in client_dictionary)
            {
                client_identifier = client_pair.Key;
                client_url = client_pair.Value;
                Console.Write("Client: ");
                Console.WriteLine(client_identifier + " / " + client_url);
            }
            foreach (Meeting meeting in meetings)
            {
                Console.Write("Meeting: ");
                Console.WriteLine(meeting.Topic);
                Console.WriteLine("Minimum atteendees: " + meeting.MinAttendees);
                Console.WriteLine("State: " + meeting.State);
                Console.WriteLine("Version: " + meeting.Version);
                Console.WriteLine("Coordinator: " + meeting.Coordinator);
                invitees = meeting.GetInvitees();

                if (invitees != null)
                {
                    foreach (string invitee in invitees)
                    {
                        Console.WriteLine("  Invitees:");
                        Console.WriteLine("  " + invitee);
                    }
                }

                Console.WriteLine("Final Slot: " + meeting.FinalSlot);
            }
        }

        public int Delay()
        {
            int delay;

            Random r = new Random();
            delay = r.Next(this.min_delay, max_delay);

            return delay;
        }

    }
}

