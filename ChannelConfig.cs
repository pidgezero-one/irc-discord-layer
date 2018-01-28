using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LunaIntegration
{
    class ChannelConfig
    {
        public string name;
        public UInt64 channelid;
        public UInt64 roleid;
        public List<UInt64> users;

        public ChannelConfig(String name)
        {
            this.name = name;
            users = new List<UInt64>();
        }

        public ChannelConfig(String name, UInt64 user)
        {
            this.name = name;
            users = new List<UInt64>();
            users.Add(user);
        }

        public void setChannelId(UInt64 channelid)
        {
            this.channelid = channelid;
        }
        public void setRoleId(UInt64 roleid)
        {
            this.roleid = roleid;
        }
        public void addUser(UInt64 userid)
        {
            users.Add(userid);
        }
    }
}
