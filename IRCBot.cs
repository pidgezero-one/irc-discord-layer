using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Xml;
using System.Configuration;
using System.Collections.Specialized;
using Discord;
using System.Threading;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace LunaIntegration
{

    //IRC structure
    //Basic idea: 1 main IRC instance for the central relay bot (the one that mirrors channel messages)
    //1 IRC instance per user who asks to participate in the mirror

    class IRCBot : IDisposable
    {
        private TcpClient IRCConnection = null;
        private IRCConfig config;
        private NetworkStream ns = null;
        private StreamReader sr = null;
        private StreamWriter sw = null;
        private long lastInput;
        public List<QueueObject> queue;
        public UInt64 discordId;
        private List<IrcCommand> LaunchCommands;

        public IRCBot(IRCConfig config, UInt64 associatedId)
        {
            this.config = config;
            this.discordId = associatedId;
            LaunchCommands = new List<IrcCommand>();
            this.queue = new List<QueueObject>(); //Queue is where all incoming IRC messages as seen by this instance are stored
        }
        public IRCBot(IRCConfig config, UInt64 associatedId, List<IrcCommand> commands)
        {
            this.config = config;
            this.discordId = associatedId;
            LaunchCommands = commands;
            this.queue = new List<QueueObject>(); //Queue is where all incoming IRC messages as seen by this instance are stored
        }

        public List<QueueObject> getDiscordQueue()
        {
            return queue;
        }

        public String getNick()
        {
            return config.nick;
        }

        public void setNick(string nick)
        {
            config.nick = nick;
        }
        public void setConfig(IRCConfig conf)
        {
            config = conf;
        }
        public void setCommands(List<IrcCommand> cmds)
        {
            LaunchCommands = null;
            LaunchCommands = cmds;
        }

        public void clearQueue()
        {
            queue.Clear();
        }

        public Boolean isConnected()
        {
            return config.joined;
        }

        public Boolean Connected()
        {
            return IRCConnection.Connected;
        }

        public void Connect()
        {
            config.joined = false;

            try
            {
                IRCConnection = new TcpClient(config.server, config.port);
            }
            catch
            {
                Console.WriteLine("Connection Error");
                throw;
            }

            try
            {
                ns = IRCConnection.GetStream();
                sr = new StreamReader(ns);
                sw = new StreamWriter(ns);
                sendData("PASS", config.password);
                sendData("NICK", config.nick);
                sendData("USER", "discordusr localhost " + ConfigurationManager.AppSettings.Get("IRCServer") + " :Discord User");
            }
            catch
            {
                Console.WriteLine("Communication error");
                throw;
            }
        }
        
        public void sendData(string cmd, string param)
        {
            if (param == null)
            {
                sw.WriteLine(cmd);
                sw.Flush();
                Console.WriteLine(cmd);
            }
            else
            {
                sw.WriteLine(cmd + " " + param);
                sw.Flush();
                Console.WriteLine(cmd + " " + param);
            }
        }

        private String getUser(String input)
        {
            string pattern = @"(?<=:).*?(?=!)"; //make sure coming from an actual user
            Regex rgx = new Regex(pattern, RegexOptions.IgnoreCase);
            MatchCollection matches = rgx.Matches(input);
            if (matches.Count > 0)
                return matches[0].Value;
            else
                return "";
        }

        private string StripFormat(string input) //remove IRC format codes
        {
            return Regex.Replace(input, @"(?:|||||(?:(?:\d+)(?:,\d+)?)?)", "");
        }

        public void IRCWork() //Main area for handling raw IRC input
        {
            string[] ex;
            string data;
            bool shouldRun = true;
            while (shouldRun)
            {
                if (Connected())
                {
                    data = sr.ReadLine();
                    Console.WriteLine(data); 
                    char[] charSeparator = new char[] { ' ' };
                    ex = data.Split(charSeparator, 4); //Split the data into 5 parts to get metadata
                    if (!config.joined)
                    {
                        if (ex[1] == "001")
                        {
                            if (discordId == 0)
                            {
                                sendData("PRIVMSG NICKSERV RECOVER", config.nick + " " + ConfigurationManager.AppSettings.Get("NickservPass"));
                                sendData("PRIVMSG NICKSERV identify", ConfigurationManager.AppSettings.Get("NickservPass"));
                            }
                            else
                            {
                                for (var i = 0; i < LaunchCommands.Count; i++)
                                {
                                    sendData(LaunchCommands[i].command, LaunchCommands[i].param);
                                }
                            }
                            sendData("JOIN", config.channel);
                            config.joined = true;
                        }
                    }

                    if (ex[0] == "PING")
                    {
                        sendData("PONG", ex[1]);
                    }
                


                    Int32 timestamp = ((Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds);
                    if (ex[1] == "NOTICE" && getUser(ex[0]) == "NickServ" && discordId == 0) //logic for responding to nickserv password attempts
                    {
                        if (ex[3].Contains("isn't registered")) //requested nick not regged
                        {
                            string[] ex2;
                            ex2 = StripFormat(ex[3]).Split(charSeparator);
                            this.queue.Add(new QueueObject("NICKSERV", timestamp, null, "false", ex2[1], discordId));
                        }
                        else //see if requested nick is regged
                        {
                            string[] ex2;
                            ex2 = StripFormat(ex[3]).Split(charSeparator);
                            if (ex2[1] == "is")
                            {
                                this.queue.Add(new QueueObject("NICKSERV", timestamp, null, "true", ex2[0].Remove(0, 1), discordId));
                            }
                        }
                    }
                    if (ex[1] == "NOTICE" && getUser(ex[0]) == "NickServ") //logic for responding to nickserv registration investigation
                    {
                        if (ex[3].Contains("Password incorrect")) //requested nick not regged
                        {
                            this.queue.Add(new QueueObject("REJECT", timestamp, null, "true", config.nick, discordId));
                            shouldRun = false;
                            config.joined = false;
                            break;
                        }
                        else if (ex[3].Contains("Password accepted")) //requested nick not regged
                        {
                            this.queue.Add(new QueueObject("ACCEPT", timestamp, null, "true", config.nick, discordId));
                        }
                    }
                    if (ex[1] == "NOTICE" && getUser(ex[0]) == "discord") //this tells the IRC thread to exit when the user leaves the server
                    {
                        if (ex[3].Contains("TERMINATE")) //requested nick not regged
                        {
                            shouldRun = false;
                            config.joined = false;
                            this.queue.Add(new QueueObject("TERMINATE", timestamp, null, "true", config.nick, discordId));
                            break;
                        }
                    }
                    //handle luna !id response
                    else if (ex.Length == 4 && (ex[1] == "PRIVMSG" || (ex[1] == "NOTICE" && ex[2] != "*"))) //handler for all channel and private messages
                    {
                        string thisUser = getUser(ex[0]);
                        string channel = ex[2];
                        if (thisUser != "OperServ") //do not send session limit msgs
                            this.queue.Add(new QueueObject("MSG", timestamp, channel, StripFormat(ex[3]), thisUser, discordId)); //Queue this message to be sent to the appropriate Discord destination on next polling thread loop
                    }

                    if (ex[1] == "INVITE")
                    {
                        string thisUser = getUser(ex[0]);
                        string channel = ex[2];
                        if (thisUser == ConfigurationManager.AppSettings.Get("AuthorizedInviter")) //Only process the invite if it comes from a user authorized to do it
                        {
                            sendData("JOIN", ex[3]);
                            this.queue.Add(new QueueObject(ex[1], timestamp, channel, ex[3], thisUser, discordId)); //Queue this message to be sent to the appropriate Discord destination on next polling thread loop
                        }
                    }

                    if (ex[1] == "JOIN")
                    {
                        string thisUser = getUser(ex[0]);
                        string channel = ex[2];
                        this.queue.Add(new QueueObject("JOIN", timestamp, channel, "", thisUser, discordId)); //Queue this message to be sent to the appropriate Discord destination on next polling thread loop
                    }

                    if (ex[1] == "PART")
                    {
                        string thisUser = getUser(ex[0]);
                        string channel = ex[2].Remove(0, 1);
                        this.queue.Add(new QueueObject("PART", timestamp, channel, "", thisUser, discordId)); //Queue this message to be sent to the appropriate Discord destination on next polling thread loop
                    }

                    if (ex[1] == "QUIT")
                    {
                        string thisUser = getUser(ex[0]);
                        this.queue.Add(new QueueObject("QUIT", timestamp, null, String.Join(" ", ex, 2, ex.Length - 2).Remove(0, 1), thisUser, discordId)); //Queue this message to be sent to the appropriate Discord destination on next polling thread loop
                    }

                    if (ex[1] == "KICK")
                    {
                        string thisUser = getUser(ex[0]);
                        string channel = ex[2].Remove(0, 1);
                        this.queue.Add(new QueueObject("KICK", timestamp, channel, "", thisUser, discordId)); //Queue this message to be sent to the appropriate Discord destination on next polling thread loop
                    }

                    //whois response
                    if (ex[1] == "401")
                    {
                        string[] ex2;
                        ex2 = ex[3].Split(charSeparator);
                        this.queue.Add(new QueueObject("WHOIS", timestamp, null, "false", ex2[0], discordId));
                    }
                    if (ex[1] == "311")
                    {
                        string[] ex2;
                        ex2 = ex[3].Split(charSeparator);
                        this.queue.Add(new QueueObject("WHOIS", timestamp, null, "true", ex2[0], discordId));
                    }
                }
            }
            return;
        }

        public void disassociate()
        {
            discordId = 1;
        }

        public void Dispose()
        {
            if (IRCConnection != null)
                IRCConnection.Close();
            if (sr != null)
                sr.Close();
            if (sw != null)
                sw.Close();
            if (ns != null)
                ns.Close();
        }
    }
}
