# irc-discord-layer
Creates a mirror of an IRC lobby for a discord server

# Required Packages (use nu-get)
Discord.Net
Microsoft.Data.Sqlite
System.Collections
System.Configuration

Running this is a .NET framework console application

# Basic idea

You need an existing discord server with a landing channel (I call it "#intro") and a lobby channel
By default, users joining for the first time will only see the #intro channel, which should only contain a bot message telling them to PM the bot to get started.
The server should have a role (I call it "enabled") that has permissions to the lobby channel. "@everyone" has everything disabled except Read Message History.
This way the only thing new users can do is read the bot's message and PM it, and go through the registration process to get into the lobby (and thereby register and authenticate with NickServ).

The program creates a new IRC connection for every user that connects through the discord bot. The server ircd and anope (assumed) will need to set an exception for the IP of where the program is hosted to allow tons of connections as well as not zline it for throttling.

When the new user PMs the bot, it prompts them for a nickname. This is the nickname that their created IRC instance will use. The bot makes sure it only uses legal characters, and also performs a whois and nickserv info to see if the name is available.
If the name is actively being used, it prompts them for another one.
If the name is offline but is registered through nickserv, it prompts them to enter the password. It then opens up a connection to test the password. It disconnects and restarts if it fails.
If the name is not taken, it prompts the user for a password longer than 6 chars with no spaces as well as an email address, and registers them with nickserv.
If registration/authentication took place, an IRC session is created with the user's selected nickname, their credentials are saved to a sqlite db, and if the bot ever disconnects and comes back online it will auto-authenticate them. The discord user is given the role to access the lobby channel.

(When asked for a password, the discord user is warned that it is not stored securely and it should be one they dont use anywhere else. They can cancel the process at any time.)

From there, all messages posted in the discord lobby channel are also posted in the IRC channel specified in app.config as the IRC user associated to that discord user.
All messages posted in the IRC channel are posted into the discord lobby channel via the bot.

If a service (like luna or SRL) is used that must create channels on the fly and invite users, it should invite the discord IRC bot user as well.
When the bot receives an invite, it creates a mirror of that channel in the server that operates in the same fashion as the lobby channel, except it also creates a role specific to that channel for access.
This role is given to any discord user whose IRC mirror is invited to the channel, or any discord user who PMs the bot to ask to join that channel, as long as the discord bot's IRC connection is in the channel.
This simulates joining a different channel on an IRC network, in that joining is opt-in and the channel isn't visible to users who have no reason to be in it.

If the channel is cleared, the discord mirror channel is deleted and the associated role is deleted.

Discord users who are authenticated through the bot can also use the bot to send PMs back and forth to IRC users.

If a user leaves the server, their IRC session is disconnected. If they come back to the server, they must attempt to connect by PMing the bot again. The bot will recognize that they already have info there and will just ask them for their password again.

If a discord user is banned from the server, their IRC session is disconnected and their discord ID will not be allowed to connect again.


# UNFINISHED
Have not actually set it to 30 char max on names
have not tested what happens when a channel is forcibly cleared. It should remove everyone's role for that channel and delete the channel, but i haven't tested it.
Currently no way to update password.
Currently no way to unregister altogether.
Not a whole lot of safeguarding against crashes or disconnects.
No way to unban users.
I'm testing this on a server I own with a game bot built by my friends that uses channel creation and invites. If you're from SRL just ignore anything that mentions "luna".
I really think channel and bot management could be implemented better, but I don't know how. Because each IRC instance operates in its own thread, when a channel or IRC connection needs to be deleted, it might mess up other threads that are depending on an index, so I just have disabling methods which would waste a lot of memory if there were hundreds of connections. Basically I'm not a good programmer.
Ultimately this is a basic prototype. I'll make a demo video eventually.