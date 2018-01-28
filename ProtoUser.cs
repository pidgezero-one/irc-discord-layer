using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LunaIntegration
{
    class ProtoUser
    {
        public int stage;
        public string nick;
        public string nickservPass;
        public string nickservEmail;
        public UInt64 discordId;
        public int nickTaken = -1;
        public int nickRegistered = -1;
        public int lunaCharacterExists = -1;
        public Boolean identifySuccess = false;
        public int loginfailure = -1;

        public ProtoUser(int stage, UInt64 discordId)
        {
            this.stage = stage;
            this.discordId = discordId;
        }
    }
}
