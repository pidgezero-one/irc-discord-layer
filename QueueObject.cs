using System;
using System.Collections.Generic;
using System.Text;

namespace LunaIntegration
{
    //queue object from IRC to Discord
    class QueueObject : ICloneable
    {
        public string type;
        public Int32 timestamp;
        public string channel;
        public string text;
        public string sender;
        public UInt64 associatedId;

        public QueueObject(String type, Int32 timestamp, string channel, string text, string sender, UInt64 associatedId)
        {
            this.type = type;
            this.timestamp = timestamp;
            this.channel = channel;
            this.text = text;
            this.sender = sender;
            this.associatedId = associatedId;
        }

        public object Clone()
        {
            throw new NotImplementedException();
        }
    }
}
