/*
Copyright (c) 2013, 2014 Paolo Patierno

All rights reserved. This program and the accompanying materials
are made available under the terms of the Eclipse Public License v1.0
and Eclipse Distribution License v1.0 which accompany this distribution. 

The Eclipse Public License is available at 
   http://www.eclipse.org/legal/epl-v10.html
and the Eclipse Distribution License is available at 
   http://www.eclipse.org/org/documents/edl-v10.php.

Contributors:
   Paolo Patierno - initial API and implementation and/or initial documentation
*/

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("M2Mqtt")]
[assembly: AssemblyDescription("MQTT Client Library for M2M communication")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Paolo Patierno")]
[assembly: AssemblyProduct("M2Mqtt")]
[assembly: AssemblyCopyright("Copyright © Paolo Patierno 2014")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// 4.3.0.3: Do not quit receive thread immediately on host close. Catch any exception in ProcessInflightThread. Log local port when triggering closing and leaving threads due to exceptions.
// 4.3.0.2: Route log level to WriteTrace delegate. Added log output for further analysis.
// 4.3.0.1: Catch all possible exception in dispatch thread loop.

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
[assembly: AssemblyVersion("4.3.0.4")]
// to avoid compilation error (AssemblyFileVersionAttribute doesn't exist) under .Net CF 3.5
#if !WindowsCE
[assembly: AssemblyFileVersion("4.3.0.4")]
#endif