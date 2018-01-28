using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LunaIntegration
{
    class IrcCommand
    {
        public string command;
        public string param;

        public IrcCommand(string command, string param)
        {
            this.command = command;
            this.param = param;
        }
    }
}
