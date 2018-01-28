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

    //IRC connection
    internal struct IRCConfig
    {
        public bool joined;
        public string server;
        public int port;
        public string nick;
        public string name;
        public string channel;
        public string password;
    }

    //discord connection

    class Program
    {
        static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();

        public async Task MainAsync()
        {

            //TODO need some way to make SURE that no IP info is sent to the user

            //TODO need to actually test user bans, has not been done

            //initialize
            List<String> ignoreNicks = new List<String>(); //Prevents bot from echoing its own discord relays back into IRC and vice versa
            List<IRCBot> bots = new List<IRCBot>(); //Contains all IRC session configs
            List<Thread> botThreads = new List<Thread>(); //Each IRC session needs to run in its own thread to detect msgs
            List<ChannelConfig> channelsetup = new List<ChannelConfig>(); //controller storing temp data for channel creation process
            List<ProtoUser> regs = new List<ProtoUser>(); //controller storing temp data for registration process
            channelsetup.Add(new ChannelConfig("luna"));
            channelsetup[0].setChannelId(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordLobbyID")));

            createIrcInstance("discord", 0); //set main relay to "0" just to make operations easier

            DiscordSocketClient _client = new DiscordSocketClient();
            await _client.LoginAsync(TokenType.Bot, ConfigurationManager.AppSettings.Get("DiscordBotToken"));
            await _client.StartAsync();

            using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
            {
                db.Open();
                String tableCommand = "CREATE TABLE IF NOT EXISTS associations (Id INTEGER PRIMARY KEY, DiscordId BIGINT, IrcNick NVARCHAR(2048), NickservPass NVARCHAR(2048), Autoconnect INTEGER, Autoidentify INTEGER)";
                SqliteCommand createTable = new SqliteCommand(tableCommand, db);
                String tableCommand2 = "CREATE TABLE IF NOT EXISTS banned_users (Id INTEGER PRIMARY KEY, DiscordId INTEGER)";
                SqliteCommand createTable2 = new SqliteCommand(tableCommand2, db);
                //Console.Write(ConfigurationManager.ConnectionStrings[db].ConnectionString);
                try
                {
                    createTable.ExecuteReader();
                    createTable2.ExecuteReader();
                }
                catch (SqliteException e)
                {
                    throw new Exception(e.Message);
                }
                db.Close();

            }

            Thread pollIRCConnections = new Thread(new ThreadStart(updateQueue));
            _client.Ready += OnReady;
            _client.Message​Received += OnMessage​Received;
            _client.ChannelCreated += OnChannelCreated;
            _client.RoleUpdated += OnRoleCreated;
            _client.UserBanned += OnUserBanned;
            _client.UserLeft += OnUserLeave;
            _client.UserJoined += OnUserJoin;

            //TODO: need to add logic for re/joining SRL channels when disconnected



            //Create a new IRC session
            //Main idea: Going to have one IRC connection created per discord user who opts to participate in the mirror
            void createIrcInstance(String nick, UInt64 discordId, List<IrcCommand> launchCommands = null)
            {
                IRCConfig conf = new IRCConfig();
                conf.name = "discord";
                conf.nick = nick;
                conf.port = 6667;
                conf.channel = ConfigurationManager.AppSettings.Get("IRCLobby");
                conf.server = ConfigurationManager.AppSettings.Get("IRCServer");
                conf.password = ConfigurationManager.AppSettings.Get("ServerPass");
                conf.joined = false;
                IRCBot bot;
                if (launchCommands != null)
                {
                    bot = new IRCBot(conf, discordId, launchCommands);
                }
                else
                {
                    bot = new IRCBot(conf, discordId);
                }
                bot.Connect();
                botThreads.Add(new Thread(new ParameterizedThreadStart(ircInstanceStart)));
                botThreads[botThreads.Count - 1].Start(bot);
                bots.Add(bot);
                ignoreNicks.Add(nick);
            }
            //unused - couldnt get it to work this way, will need some way of clearing unused threads...
            void reconnectIrcInstance(int index, String nick, List<IrcCommand> launchCommands = null)
            {

                IRCConfig conf = new IRCConfig();
                conf.name = "discord";
                conf.nick = nick;
                conf.port = 6667;
                conf.channel = ConfigurationManager.AppSettings.Get("IRCLobby");
                conf.server = ConfigurationManager.AppSettings.Get("IRCServer");
                conf.password = ConfigurationManager.AppSettings.Get("ServerPass");
                conf.joined = false;
                bots[index].setConfig(conf);
                bots[index].setCommands(launchCommands);
                bots[index].Connect();
                Console.WriteLine(bots.Count);
                botThreads.Add(new Thread(new ParameterizedThreadStart(ircInstanceStart)));
                botThreads[botThreads.Count - 1].Start(bots[index]);
                Console.WriteLine(botThreads.Count);
                if (!ignoreNicks.Contains(nick))
                    ignoreNicks.Add(nick);
            }
            void ircInstanceStart(object ircbot)
            {
                IRCBot ibot = (IRCBot)ircbot;
                using (ibot)
                {
                    ibot.IRCWork(); //IRCWork is the main powerhouse of the IRCBot class that retrieves and processes msgs
                }
            }


            //This is the logic that retrieves inbound messages from each IRC session
            //It is a thread that forever loops through all IRC bots to process their inbound queues and
            //deliver messages to the appropriate discord destinations
            void newChannel(string channelName, UInt64 user = 0)
            {
                int index;
                try
                {
                    index = channelsetup.FindIndex(a => a.name == channelName);
                }
                catch
                {
                    index = -1;
                }
                if (index >= 0 && index < channelsetup.Count && user != 0)
                {
                    channelsetup[index].addUser(user);
                }
                else if (user == 0)
                {
                    channelsetup.Add(new ChannelConfig(channelName));
                    var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                    server.CreateTextChannelAsync(channelName);
                }
                else
                {
                    channelsetup.Add(new ChannelConfig(channelName, user));
                }
            }

            //this is the function that checks every opened IRC connection for incoming messages that were processed by IRCBot as commands
            void updateQueue()
            {
                List<QueueObject> incomingIrc = new List<QueueObject>();
                while (true)
                {
                    incomingIrc.Clear();
                    try
                    {
                        for (var i = 0; i < bots.Count; i++)
                        {
                            //copy list for this irc bot instance -- bc this is an async thread,
                            //contents of the queue may change while looping, dont wanna delete
                            //any unsent ones by accident
                            var q = bots[i].queue;
                            var num = q.Count;
                            if (num > 0)
                            {
                                var botQueue = new List<QueueObject>(num);
                                //this double loop may not be necessary and i don't remember why i added it
                                for (var j = 0; j < num; j++)
                                {
                                    botQueue.Add(q[j]);
                                }
                                for (var j = 0; j < botQueue.Count; j++)
                                {
                                    incomingIrc.Add(botQueue[j]);
                                }
                                //delete all msgs from this irc bot queue that we just copied, but not any others that may have come in
                                for (var j = num - 1; j >= 0; j--)
                                {
                                    bots[i].queue.RemoveAt(j);
                                }

                            }
                        }
                        //Now sort all retrieved messages chronologically to make sure they are output in the correct order
                        List<QueueObject> ordered = incomingIrc.OrderBy(o => o.timestamp).ToList();
                        foreach (var orderedCommand in ordered)
                        {
                            //IRC to Discord message delivery
                            if (orderedCommand.type == "MSG")
                            {
                                if (orderedCommand.channel.Substring(0, 1) == "#" && orderedCommand.associatedId == 0) //channel mirroring: only the main relay bot needs to send channel msgs
                                {
                                    //lobby handling
                                    if (orderedCommand.channel == ConfigurationManager.AppSettings.Get("IRCLobby"))
                                    {
                                        //build stripper for font format codes
                                        var channel = _client.GetChannel(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordLobbyID"))) as SocketTextChannel;
                                        if (!ignoreNicks.Contains(orderedCommand.sender))
                                        {
                                            channel.SendMessageAsync("<" + orderedCommand.sender + "> " + orderedCommand.text.Remove(0, 1));
                                        }
                                    }
                                    else //handling for party channels which are created on the fly to mirror irc ones, need to be found in channelsetup index
                                    {
                                        int index = channelsetup.FindIndex(a => a.name == orderedCommand.channel.Remove(0, 1));
                                        if (index >= 0 && index < channelsetup.Count)
                                        {
                                            var channel = _client.GetChannel(channelsetup[index].channelid) as SocketTextChannel;
                                            if (!ignoreNicks.Contains(orderedCommand.sender))
                                            {
                                                channel.SendMessageAsync("<" + orderedCommand.sender + "> " + orderedCommand.text.Remove(0, 1));
                                            }
                                        }
                                    }
                                    //upgrade for other channels also
                                }
                                else if (orderedCommand.channel.Substring(0, 1) != "#") //PM mirroring: discord bot should relay to discord user
                                {
                                    //relay a pm to the discord user from their irc session
                                    int index = bots.FindIndex(a => a.discordId == orderedCommand.associatedId);
                                    if (index > 0 && index < bots.Count)
                                    {
                                        var channel = _client.GetUser(bots[index].discordId) as SocketUser;
                                        channel.SendMessageAsync("<" + orderedCommand.sender + "> " + orderedCommand.text.Remove(0, 1));
                                    }
                                }
                            }

                            //IRC channel invite handler
                            else if (orderedCommand.type == "INVITE")
                            {
                                String newChannelName = orderedCommand.text.Remove(0, 2);
                                if (orderedCommand.associatedId == 0) //create the associated discord mirror channel, config logic will happen in a channelcreated task
                                {
                                    newChannel(newChannelName);
                                }
                                else //logic to assign the role to an invitee to the associated discord mirror channel
                                {
                                    newChannel(newChannelName, orderedCommand.associatedId);
                                }
                            }
                            else if (orderedCommand.type == "JOIN" && orderedCommand.associatedId == 0)
                            {
                                int index = channelsetup.FindIndex(a => a.name == orderedCommand.channel.Remove(0, 2));
                                if (index >= 0 && index < channelsetup.Count)
                                {
                                    var ch = _client.GetChannel(channelsetup[index].channelid) as SocketTextChannel;
                                    if (!ignoreNicks.Contains(orderedCommand.sender))
                                    {
                                        ch.SendMessageAsync("*Joins: " + orderedCommand.sender + "*");
                                    }
                                }
                            }
                            else if (orderedCommand.type == "PART" && orderedCommand.associatedId == 0)
                            {
                                int index = channelsetup.FindIndex(a => a.name == orderedCommand.channel);
                                if (index >= 0 && index < channelsetup.Count)
                                {
                                    var ch = _client.GetChannel(channelsetup[index].channelid) as SocketTextChannel;
                                    if (!ignoreNicks.Contains(orderedCommand.sender))
                                    {
                                        ch.SendMessageAsync("*Parts: " + orderedCommand.sender + "*");
                                    }
                                }
                            }
                            else if (orderedCommand.type == "QUIT" && orderedCommand.associatedId == 0)
                            {
                                var ch = _client.GetChannel(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordLobbyID"))) as SocketTextChannel;
                                if (!ignoreNicks.Contains(orderedCommand.sender))
                                {
                                    ch.SendMessageAsync("*Quits: " + orderedCommand.sender + " (" + orderedCommand.text + ")*");
                                }
                            }
                            else if (orderedCommand.type == "WHOIS") //handler to check if name 
                            {
                                int index = regs.FindIndex(a => a.nick == orderedCommand.sender);
                                if (index >= 0 && index < regs.Count)
                                {
                                    if (orderedCommand.text == "false")
                                    {
                                        regs[index].nickTaken = 0;
                                    }
                                    else
                                    {
                                        regs[index].nickTaken = 1;
                                    }
                                }
                            }
                            else if (orderedCommand.type == "NICKSERV")
                            {
                                int index = regs.FindIndex(a => a.nick == orderedCommand.sender);
                                if (index >= 0 && index < regs.Count)
                                {
                                    if (orderedCommand.text == "false")
                                    {
                                        regs[index].nickRegistered = 0;
                                    }
                                    else
                                    {
                                        regs[index].nickRegistered = 1;
                                    }
                                }
                            }
                            else if (orderedCommand.type == "REJECT")
                            {
                                int index = regs.FindIndex(a => a.nick == orderedCommand.sender);
                                if (index >= 0 && index < regs.Count)
                                {
                                    regs[index].loginfailure = 1;
                                }
                            }
                            else if (orderedCommand.type == "ACCEPT")
                            {
                                int index = regs.FindIndex(a => a.nick == orderedCommand.sender);
                                if (index >= 0 && index < regs.Count)
                                {
                                    regs[index].loginfailure = 0;
                                }
                            }
                            else if (orderedCommand.type == "TERMINATE")
                            {
                                int index2 = bots.FindIndex(a => a.discordId == orderedCommand.associatedId);
                                if (index2 >= 0 && index2 < bots.Count)
                                {
                                    bots[index2].Dispose();
                                    bots[index2].disassociate();
                                }
                            }
                            else if (orderedCommand.type == "KICK")
                            {
                                if (orderedCommand.associatedId == 0) //if relay bot is kicked, destroy mirror channel
                                {
                                    destroyChannel(orderedCommand.channel);
                                }
                                else //if another connected IRC user is kicked, remove their access from the discord channel mirroring it
                                {
                                    int channelIndex = channelsetup.FindIndex(a => a.name == orderedCommand.channel);
                                    if (channelIndex >= 0 && channelIndex < channelsetup.Count)
                                    {
                                        int userIndex = channelsetup[channelIndex].users.IndexOf(orderedCommand.associatedId);
                                        if (userIndex >= 0 && userIndex < channelsetup[channelIndex].users.Count)
                                        {
                                            channelsetup[channelIndex].users.RemoveAt(userIndex);
                                        }
                                        var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID")));
                                        server.GetUser(orderedCommand.associatedId).RemoveRoleAsync(server.GetRole(channelsetup[channelIndex].roleid));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.ToString());
                        incomingIrc.Clear();
                    }
                }
            }

            //start: init main IRC and connect everyone already in the server if registered
            Task OnReady()
            {
                using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
                {
                    db.Open();
                    //if this program got disconnected, attempt to connect all users online in the guild
                    var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                    var users = server.Users;

                    //set intro channel message - only needs to run once
                    //server.GetTextChannel(406619579176189953).SendMessageAsync("Welcome!\r\n\r\nThis server mirrors **" + ConfigurationManager.AppSettings.Get("IRCLobby") + "** on **" + ConfigurationManager.AppSettings.Get("IRCServer") + "**. You can use Discord to interact with what's going on in this IRC server, but first you will need to provide the bot (this discord account) some info.\r\n\r\nClick this bot's name and DM it privately with **-help** to get started.");

                    List<String> userIds = new List<String>();

                    foreach (SocketGuildUser user in users)
                    {
                        userIds.Add(user.Id.ToString());
                    }

                    SqliteCommand getCredentials = new SqliteCommand("SELECT * FROM associations LEFT JOIN banned_users ON associations.DiscordId = banned_users.DiscordId WHERE associations.DiscordId IN (" + String.Join(", ", userIds) + ") AND banned_users.DiscordId IS NULL", db);
                    SqliteDataReader query;
                    try
                    {
                        query = getCredentials.ExecuteReader();
                        while (query.Read())
                        {
                            List<IrcCommand> command = new List<IrcCommand>();
                            command.Add(new IrcCommand("PRIVMSG NickServ RECOVER", query.GetString(2) + " " + query.GetString(3)));
                            command.Add(new IrcCommand("PRIVMSG NickServ IDENTIFY", query.GetString(3)));

                            createIrcInstance(query.GetString(2), (UInt64)query.GetInt64(1), command);

                            server.GetUser((UInt64)query.GetInt64(1)).AddRoleAsync(server.GetRole(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordLobbyRoleID"))));
                        }
                        pollIRCConnections.Start();
                        return Task.CompletedTask;
                        //not sure how to handle deleting regs index and have it not screw up other threads relying on index
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        pollIRCConnections.Start();
                        return Task.CompletedTask;
                    }
                    db.Close();

                }

            }


            //discord msg handling
            //needs to wait for whois/nickserv responses, so handling messages in its own thread
            void IncomingMessageHandler(object msg)
            {
                SocketMessage message = (SocketMessage)(msg);
                String method = message.Channel.ToString();
                char[] charSeparator = new char[] { '#' };
                string[] ex = message.Author.ToString().Split(charSeparator);
                var discordid = ex[ex.Length - 1];
                var username = Regex.Replace(message.Author.ToString().Replace("#" + discordid, ""), @"[^A-z0-9`\{\}\[\]\|\^_\-]", "");

                if (discordid != ConfigurationManager.AppSettings.Get("DiscordBotDiscriminator")) //ignore own messages
                {
                    var connected = false;
                    //check if this user has an IRC session
                    int index = bots.FindIndex(a => a.discordId == message.Author.Id);
                    if (index > 0 && index < bots.Count)
                    {
                        connected = bots[index].Connected();
                    }
                    else
                    {
                        connected = false;
                    }
                    //handler for receiving a PM from discord user
                    if (method.Substring(0, 1) == "@")
                    {
                        //TODO need an engine to update password

                        //TODO need mercy kills on while loops that await nickserv/whois responses


                        //handler to create a new irc instance

                        /* here's the basic idea from an email i wrote to myself in late jan...
                         * create new ProtoUser class that has int stage, string nick, string nickservPass, string nickservEmail, UInt64 discordId, nickTaken = -1, nickRegistered = -1, lunaCharacterExists = -1, identifySuccess = false
                         * add logic to ircbot receive data that returns whois responses and nickserv notice responses and !id responses
                         * add these to queue and handle in messagereceived
                         * when received and processed in queue, filter protousers to match nick, and set nickTaken, nickRegistered, or lunaCharacterExists to 0 or 1 depending on content
                         * 	add protousers class to main thread
                         * 	when receive message /connect:
                         * 		create new ProtoUser class with stage 0, discordId user id
                         * 		ask user "First, choose a name that you would like to use. Can include letters, numbers, and any of `^[]{}\|, but cannot start with a number and must be 30 chars or less."
                         * 		validate name
                         * 		if it's not valid, tell user "Username wasn't valid, please only use  letters, numbers, and any of `^[]{}\|, less than 30 characters and not starting with a number."
                         * 		if it is valid:
                         * 			sendData from main irc thread to whois that name, ns info that name, and !id that name
                         * 			run a while loop that doesn't end until protouser nickTaken, nickRegistered, and lunaCharacterExists are all not -1
                         * 			if nickTaken is 1, tell them to choose another nick
                         * 			if nickRegistered is 1, prompt them for password, attempt to connect and ns identify (run another while loop waiting for this response, set identifySuccess to true if it worked)
                         * 			if identified, skip next steps and give them user enabled role, otherwise ask them to try again, close IRC session and cancel reg process on 3 fialed attempts
                         * 		ask user Now choose a password so nobody else can use your name. WARNING! Passwords are not stored or transmitted securely. Please make sure it is not a password you use anywhere else. If you're not comfortable with this, please type **cancel** now and use IRC to register.
                         * 		if password is not valid (too short?) warn them
                         * 		if it is valid, advance to stage 3
                         * 		ask user Now enter a valid e-mail address belonging to you that you can use for registering your name. (You will not receive email from any IRC services unless requested.)
                         * 		if email is not valid warn them
                         * 		if it is valid, run IRC connection process and register upon connecting, then give user enabled role
                         * 		if this is luna (or SRL i guess), give them basic instructions for registering a character
                         * 		save their data to db
                         * 	when receive message /cancel:
                         * 		delete ProtoUser object if one exists
                         * 		tell user "If you would liek to use IRC to register instead, follow this link for instructions: <srl link>" if SRL bot
                         */


                        if (message.ToString() == "/help") //initiate mirror process for a user
                        {
                            message.Channel.SendMessageAsync("I am a bot that mirrors functionality for " + ConfigurationManager.AppSettings.Get("IRCLobby") + " on " + ConfigurationManager.AppSettings.Get("IRCServer") + "\r\n\r\n" + "If you are not in the lobby channel on discord already, type **/connect** to get started. " + "If you are already connected, you can message me with **/join *#channel-name*** to join an IRC channel or **/msg *username __your message here__*** to send a PM to an IRC user.");
                        }
                        else if (message.ToString() == "/connect") //initiate mirror process for a user
                        {
                            if (connected) //if this user already has an irc instance connected this command is useless
                            {
                                message.Channel.SendMessageAsync("You are already connected.");
                            }
                            else
                            {

                                //see if this discord ID has a name and password set in the database already, prompt for password if they do
                                using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
                                {
                                    db.Open();
                                    //check if banned first
                                    SqliteCommand isbanned = new SqliteCommand("SELECT * FROM banned_users WHERE DiscordId = @id", db);
                                    isbanned.Parameters.AddWithValue("@id", message.Author.Id);
                                    SqliteDataReader query1;
                                    try
                                    {
                                        Boolean banned = false;
                                        query1 = isbanned.ExecuteReader();
                                        while (query1.Read())
                                        {
                                            banned = true;
                                        }
                                        if (!banned)
                                        {
                                            //check if this user already has an associated connection
                                            var protoIndex = regs.FindIndex(a => a.discordId == message.Author.Id);
                                            if (!(protoIndex >= 0 && protoIndex < regs.Count))
                                            {
                                                regs.Add(new ProtoUser(0, message.Author.Id));
                                                protoIndex = regs.FindIndex(a => a.discordId == message.Author.Id);
                                            }
                                            else
                                                regs[protoIndex].stage = 0;

                                            SqliteCommand tc = new SqliteCommand("SELECT * FROM associations WHERE DiscordId = @id", db);
                                            tc.Parameters.AddWithValue("@id", message.Author.Id);
                                            SqliteDataReader query;
                                            try
                                            {
                                                Boolean exists = false;
                                                String existingName = "";
                                                query = tc.ExecuteReader();
                                                while (query.Read())
                                                {
                                                    exists = true;
                                                    existingName = query.GetString(2);
                                                }
                                                if (exists)
                                                {
                                                    regs[protoIndex].stage = -2; //set to state that prompts for password
                                                    regs[protoIndex].nick = existingName;
                                                    message.Channel.SendMessageAsync("You have already registered as **" + existingName + "**. Enter your password to connect:");
                                                }
                                                else //otherwise initiate startup process
                                                {
                                                    regs[protoIndex].stage = 1; //set to state that prompts for password
                                                    message.Channel.SendMessageAsync("You are starting the signup process now. At any point, if you wish to stop, please type **/cancel**\r\n\r\nEnter a name that you would like to use. Can include letters, numbers, and any of `^[]{}\\\\|-_, but cannot start with a number and must be 30 chars or less.");
                                                }
                                            }
                                            catch (Exception e)
                                            {
                                                Console.WriteLine("1- " + e.Message);
                                            }
                                        }
                                        else //otherwise initiate startup process
                                        {
                                            message.Channel.SendMessageAsync("You have previously been banned and may not connect.");
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine("1- " + e.Message);
                                    }
                                    db.Close();
                                }
                            }
                        }
                        else
                        {
                            String input = message.ToString();
                            char[] charSeparator2 = new char[] { ' ' };
                            string[] inputparams = input.Split(charSeparator2);
                            if (connected)
                            {
                                if (inputparams[0] == "/msg")
                                {
                                    bots[index].sendData("PRIVMSG " + inputparams[1], String.Join(" ", inputparams, 2, inputparams.Length - 2));
                                }
                                else if (inputparams[0] == "/join")
                                {
                                    String ircchannel;
                                    String discordchannel;
                                    if (inputparams[1].Substring(0,1) == "#")
                                    {
                                        ircchannel = inputparams[1];
                                        discordchannel = inputparams[1].Remove(0, 1);
                                    }
                                    else
                                    {
                                        ircchannel = "#" + inputparams[1];
                                        discordchannel = inputparams[1];
                                    }
                                    //check if channel exists on discord server
                                    int channelIndex = channelsetup.FindIndex(a => a.name == discordchannel);
                                    if (channelIndex >= 0 && channelIndex < channelsetup.Count)
                                    {
                                        //TODO make it so that it confirms that irc user can join channel first (i.e. not +i) before giving role
                                        var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                                        var user = server.GetUser(message.Channel.Id);
                                        user.AddRoleAsync(server.GetRole(channelsetup[channelIndex].roleid));
                                    }
                                    else
                                    {
                                        message.Channel.SendMessageAsync("Cannot join that channel.");
                                    }
                                    bots[index].sendData("JOIN", inputparams[1]);
                                }
                            }
                            else
                            {
                                var protoIndex = regs.FindIndex(a => a.discordId == message.Author.Id);
                                if (protoIndex >= 0 && protoIndex < regs.Count)
                                {
                                    if (input == "/cancel")
                                    {
                                        //not sure how to handle deleting regs index and have it not screw up other threads relying on index
                                        regs[protoIndex].stage = 0;
                                        message.Channel.SendMessageAsync("Registration process has been cancelled.");
                                    }
                                    else if (regs[protoIndex].stage == 1) //getting name
                                    {
                                        regs[protoIndex].nick = input;
                                        regs[protoIndex].nickTaken = -1;
                                        regs[protoIndex].nickRegistered = -1;
                                        regs[protoIndex].lunaCharacterExists = -1;
                                        regs[protoIndex].loginfailure = -1;
                                        string pattern = @"[^A-z0-9`\^\[\]\{\}\\|\-_]";
                                        Regex rgx = new Regex(pattern, RegexOptions.IgnoreCase);
                                        MatchCollection matches = rgx.Matches(input);
                                        if (matches.Count > 0)
                                        {
                                            message.Channel.SendMessageAsync("Name contains illegal characters, please try again.");
                                        }
                                        else
                                        {
                                            bots[0].sendData("WHOIS", input);
                                            while (regs[protoIndex].nickTaken == -1) ;
                                            if (regs[protoIndex].nickTaken == 1)
                                            {
                                                message.Channel.SendMessageAsync("Someone else is connected with that name, please try another one.");
                                            }
                                            else
                                            {
                                                bots[0].sendData("PRIVMSG", "NickServ info " + input);
                                                while (regs[protoIndex].nickRegistered == -1) ;
                                                if (regs[protoIndex].nickRegistered == 1)
                                                {
                                                    message.Channel.SendMessageAsync("That nickname is already registered. If it belongs to you, you can enter your password here to authenticate. **WARNING! Passwords are not stored or transmitted securely. If you're not comfortable with this, or if this isn't your name, please cancel the signup process.** Otherwise, please type your password.");
                                                    regs[protoIndex].stage = -1;
                                                }
                                                else
                                                {
                                                    /*bots[0].sendData("PRIVMSG luna", "!id " + orderedCommand.sender);
                                                    while (regs[protoIndex].lunaCharacterExists == -1)
                                                    {

                                                    }
                                                    if (regs[protoIndex].lunaCharacterExists == 1)
                                                    {
                                                        message.Channel.SendMessageAsync("A Luna character already exists with that name, please try another one.");
                                                    }
                                                    else
                                                    {*/
                                                    regs[protoIndex].stage = 2;
                                                    message.Channel.SendMessageAsync("Please enter a password (minimum 6 characters, no spaces) you would like to use to sign into NickServ, a service IRC uses to prevent impersonation. **WARNING! Passwords are not stored or transmitted securely. Please make sure it is not a password you use anywhere else. If you're not comfortable with this, please cancel the signup process.** Otherwise, please type your password.");
                                                    //}
                                                }
                                            }
                                        }
                                    }
                                    else if (regs[protoIndex].stage == -2) //getting password for logging in to already regged nick that's already recognized by the db
                                    {
                                        using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
                                        {
                                            db.Open();
                                            SqliteCommand tc = new SqliteCommand("SELECT * FROM associations WHERE DiscordId = @id", db);
                                            tc.Parameters.AddWithValue("@id", message.Author.Id);
                                            SqliteDataReader query;
                                            try
                                            {
                                                String existingPass = "";
                                                query = tc.ExecuteReader();
                                                while (query.Read())
                                                {
                                                    existingPass = query.GetString(3);
                                                }
                                                if (input == existingPass)
                                                {
                                                    regs[protoIndex].nickservPass = existingPass;
                                                    message.Channel.SendMessageAsync("Login successful. You should now have access to the server lobby.");
                                                    List<IrcCommand> command = new List<IrcCommand>();
                                                    command.Add(new IrcCommand("PRIVMSG NickServ RECOVER", regs[protoIndex].nick + " " + regs[protoIndex].nickservPass));
                                                    command.Add(new IrcCommand("PRIVMSG NickServ IDENTIFY", regs[protoIndex].nickservPass));

                                                    createIrcInstance(regs[protoIndex].nick, regs[protoIndex].discordId, command);

                                                    //give lobby access role
                                                    var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                                                    var user = server.GetUser(regs[protoIndex].discordId);
                                                    user.AddRoleAsync(server.GetRole(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordLobbyRoleID"))));

                                                    regs[protoIndex].stage = 4;
                                                }
                                                else
                                                {
                                                    message.Channel.SendMessageAsync("Password incorrect. Please enter it again:");
                                                }
                                            }
                                            catch (Exception e)
                                            {
                                                Console.WriteLine(e.Message);
                                            }
                                            db.Close();
                                        }
                                    }
                                    else if (regs[protoIndex].stage == -1) //getting password for logging in to already regged nick that's not in the db yet
                                    {
                                        regs[protoIndex].nickservPass = input;

                                        //attempt to connect with this password
                                        List<IrcCommand> command = new List<IrcCommand>();
                                        command.Add(new IrcCommand("PRIVMSG NickServ RECOVER", regs[protoIndex].nick + " " + regs[protoIndex].nickservPass));
                                        command.Add(new IrcCommand("PRIVMSG NickServ IDENTIFY", regs[protoIndex].nickservPass));

                                        createIrcInstance(regs[protoIndex].nick, regs[protoIndex].discordId, command);

                                        //wait for nickserv response
                                        while (regs[protoIndex].loginfailure == -1) ;
                                        //launch connection and try to sign in, quit if failed
                                        if (regs[protoIndex].loginfailure == 0)
                                        {
                                            message.Channel.SendMessageAsync("Login successful. You should now have access to the server lobby.");

                                            //give lobby access role
                                            var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                                            var user = server.GetUser(regs[protoIndex].discordId);
                                            user.AddRoleAsync(server.GetRole(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordLobbyRoleID"))));

                                            regs[protoIndex].stage = 4;

                                            using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
                                            {
                                                db.Open();
                                                SqliteCommand tc = new SqliteCommand("INSERT INTO associations (DiscordId, IrcNick, NickservPass) VALUES (@id, @nick, @pass)", db);
                                                tc.Parameters.AddWithValue("@id", regs[protoIndex].discordId);
                                                tc.Parameters.AddWithValue("@nick", regs[protoIndex].nick);
                                                tc.Parameters.AddWithValue("@pass", regs[protoIndex].nickservPass);
                                                try
                                                {
                                                    tc.ExecuteNonQuery();
                                                    //not sure how to handle deleting regs index and have it not screw up other threads relying on index
                                                }
                                                catch (Exception e)
                                                {
                                                    Console.WriteLine(e.Message);
                                                }
                                                db.Close();
                                            }

                                        }
                                        else
                                        {
                                            int index2 = bots.FindIndex(a => a.discordId == regs[protoIndex].discordId);
                                            if (index2 >= 0 && index2 < bots.Count)
                                            {
                                                message.Channel.SendMessageAsync("Password incorrect. Please type **/connect** to start over.");
                                                regs[protoIndex].stage = 0;
                                                bots[index2].Dispose();
                                                bots[index2].disassociate();
                                            }
                                        }
                                    }
                                    else if (regs[protoIndex].stage == 2) //getting password for new nick
                                    {
                                        if (input.Length > 5 && !input.Contains(" "))
                                        {
                                            regs[protoIndex].nickservPass = input;
                                            regs[protoIndex].stage = 3;
                                            message.Channel.SendMessageAsync("Please enter a valid e-mail address belonging to you that you can use for registering your name. (You will not receive email from any IRC services unless requested.)");
                                        }
                                        else
                                        {
                                            message.Channel.SendMessageAsync("Passwords must be at least 6 characters and cannot contain spaces. Please enter another password.");
                                        }
                                    }
                                    else if (regs[protoIndex].stage == 3) //getting email for new nick
                                    {
                                        string pattern = @"^([\w\.\-]+)@([\w\-]+)((\.(\w){2,3})+)$";
                                        Regex rgx = new Regex(pattern, RegexOptions.IgnoreCase);
                                        Match matches = rgx.Match(input);
                                        if (matches.Success)
                                        {
                                            regs[protoIndex].nickservEmail = input;
                                            List<IrcCommand> command = new List<IrcCommand>();
                                            command.Add(new IrcCommand("PRIVMSG NickServ REGISTER", regs[protoIndex].nickservPass + " " + regs[protoIndex].nickservEmail));

                                            createIrcInstance(regs[protoIndex].nick, regs[protoIndex].discordId, command);

                                            //give lobby access role
                                            var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                                            var user = server.GetUser(regs[protoIndex].discordId);
                                            user.AddRoleAsync(server.GetRole(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordLobbyRoleID"))));

                                            //save to DB for autologin in future
                                            using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
                                            {
                                                db.Open();
                                                SqliteCommand tc = new SqliteCommand("INSERT INTO associations (DiscordId, IrcNick, NickservPass) VALUES (@id, @nick, @pass)", db);
                                                tc.Parameters.AddWithValue("@id", regs[protoIndex].discordId);
                                                tc.Parameters.AddWithValue("@nick", regs[protoIndex].nick);
                                                tc.Parameters.AddWithValue("@pass", regs[protoIndex].nickservPass);
                                                try
                                                {
                                                    tc.ExecuteNonQuery();
                                                    //not sure how to handle deleting regs index and have it not screw up other threads relying on index
                                                }
                                                catch (Exception e)
                                                {
                                                    Console.WriteLine(e.Message);
                                                }
                                                db.Close();
                                            }

                                            message.Channel.SendMessageAsync("Successfully registered. You will be logged in automatically when you come online on Discord. You should now have access to the server lobby.");
                                            regs[protoIndex].stage = 4;
                                        }
                                        else
                                        {
                                            message.Channel.SendMessageAsync("E-mail format was not valid, please enter another e-mail address.");
                                        }
                                    }
                                    else if (regs[protoIndex].stage == 4)
                                    {
                                        //not needed since "if connected" clause takes over here
                                    }
                                }
                                //message.Channel.SendMessageAsync("You are not connected to IRC.");
                            }
                        }
                    }

                    //handler for channel messages
                    else
                    {
                        //integrate nickflashing
                        //if user doenst have an associated irc session, send via the relay bot, otherwise send as that user's IRC session
                        if (connected)
                        {
                            bots[index].sendData("PRIVMSG #" + message.Channel.Name, message.ToString());
                        }
                        else
                        {
                            bots[0].sendData("PRIVMSG #" + message.Channel.Name, "<" + message.Author.ToString().Replace("#" + discordid, "") + "> " + message.ToString());
                        }
                    }

                }
            }
            Task OnMessage​Received(SocketMessage message)
            {
                var t = new Thread(new ParameterizedThreadStart(IncomingMessageHandler));
                t.Start(message);
                return Task.CompletedTask;
            }


            //invite handling: create channel
            Task OnChannelCreated(SocketChannel channel)
            {
                try //only fire if it's a server channel
                {
                    if (channel.GetType().ToString() != "Discord.WebSocket.SocketDMChannel")
                    {
                        var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                        server.CreateRoleAsync(channel.ToString());
                        var index = -1;
                        while (!(index >= 0 && index < channelsetup.Count))
                        {
                            index = channelsetup.FindIndex(a => a.name == channel.ToString());
                        }
                        channelsetup[index].setChannelId(channel.Id);
                    }
                }
                catch (Exception e)
                {

                }
                return Task.CompletedTask;
            }

            //delete a text channel, delete the associated role, make everyone part the irc channel
            void destroyChannel(string channelName)
            {
                int channelIndex = channelsetup.FindIndex(a => a.name == channelName);
                if (channelIndex >= 0 && channelIndex < channelsetup.Count)
                {
                    foreach (UInt64 id in channelsetup[channelIndex].users)
                    {
                        int botIndex = bots.FindIndex(a => a.discordId == id);
                        if (botIndex >= 0 && botIndex < bots.Count)
                        {
                            bots[botIndex].sendData("PART", "#" + channelName);
                        }
                    }
                    var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID")));
                    server.GetRole(channelsetup[channelIndex].roleid).DeleteAsync();
                }
            }


            //continuation of invite handling: create role to match channel
            Task OnRoleCreated(SocketRole role1, SocketRole role2)
            {
                var server = _client.GetGuild(UInt64.Parse(ConfigurationManager.AppSettings.Get("DiscordServerID"))) as SocketGuild;
                var index = -1;
                while (!(index >= 0 && index < channelsetup.Count))
                {
                    index = channelsetup.FindIndex(a => a.name == role2.Name);
                }
                channelsetup[index].setRoleId(role2.Id);
                server.GetTextChannel(channelsetup[index].channelid).AddPermissionOverwriteAsync(role2, new OverwritePermissions(PermValue.Inherit, PermValue.Inherit, PermValue.Inherit, PermValue.Allow, PermValue.Allow, PermValue.Inherit, PermValue.Inherit, PermValue.Inherit, PermValue.Inherit, PermValue.Allow, PermValue.Inherit, PermValue.Inherit, PermValue.Allow, PermValue.Inherit, PermValue.Inherit, PermValue.Inherit, PermValue.Inherit, PermValue.Inherit, PermValue.Inherit, PermValue.Inherit));
                //restrict from @everyone
                //this can be done by removing the read text channels permission from users by default
                foreach (var userid in channelsetup[index].users)
                {
                    server.GetUser(userid).AddRoleAsync(role2);
                }
                return Task.CompletedTask;
            }


            //when user joins server, check if it's a re/join
            Task OnUserJoin(SocketGuildUser user)
            {
                using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
                {
                    db.Open();
                    SqliteCommand getCredentials = new SqliteCommand("SELECT * FROM associations LEFT JOIN banned_users ON associations.DiscordId = banned_users.DiscordId WHERE DiscordId = (" + user.Id + ") AND banned_users.DiscordId IS NULL", db);
                    SqliteDataReader query;
                    try
                    {
                        query = getCredentials.ExecuteReader();
                        while (query.Read())
                        {
                            List<IrcCommand> command = new List<IrcCommand>();
                            command.Add(new IrcCommand("PRIVMSG NickServ RECOVER", query.GetString(2) + " " + query.GetString(3)));
                            command.Add(new IrcCommand("PRIVMSG NickServ IDENTIFY", query.GetString(3)));

                            createIrcInstance(query.GetString(2), (UInt64)query.GetInt64(1), command);
                        }
                        //not sure how to handle deleting regs index and have it not screw up other threads relying on index
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                    db.Close();
                    return Task.CompletedTask;

                }
            }

            //disconnect a user's IRC session if they leave
            Task OnUserLeave(SocketGuildUser user)
            {
                int index2 = bots.FindIndex(a => a.discordId == user.Id);
                if (index2 >= 0 && index2 < bots.Count)
                {
                    bots[0].sendData("NOTICE", bots[index2].getNick() + " TERMINATE");
                }
                return Task.CompletedTask;
            }

            //TODO test banned user connection prevention

            //disconnect a user's IRC session and add them to the banned list if banned
            void banDiscordId(UInt64 id)
            {
                using (SqliteConnection db = new SqliteConnection("Filename=luna_associations.db"))
                {
                    db.Open();
                    SqliteCommand tc = new SqliteCommand("INSERT INTO banned_users (DiscordId) VALUES (@id)", db);
                    tc.Parameters.AddWithValue("@id", id);
                    try
                    {
                        tc.ExecuteNonQuery();
                        //not sure how to handle deleting regs index and have it not screw up other threads relying on index
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                    db.Close();
                }
            }
            Task OnUserBanned(SocketUser user, SocketGuild server)
            {
                int index2 = bots.FindIndex(a => a.discordId == user.Id);
                if (index2 >= 0 && index2 < bots.Count)
                {
                    bots[0].sendData("NOTICE", bots[0].getNick() + " TERMINATE");
                    banDiscordId(user.Id);
                }
                return Task.CompletedTask;
            }



            await Task.Delay(-1);
        }

    }
}
