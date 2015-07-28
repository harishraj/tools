﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

// https://msdn.microsoft.com/en-us/library/zt39148a(v=vs.110).aspx

namespace Org.SwerveRobotics.BlueBotBug.Service
    {
    static class Program
        {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
            {
            DecompiledServiceBase[] ServicesToRun;
            ServicesToRun = new DecompiledServiceBase[] 
                { 
                new BlueBotBugService() 
                };
            DecompiledServiceBase.Run(ServicesToRun);
            }
        }
    }
