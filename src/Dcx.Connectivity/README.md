# Dcx.Connectivity

Reaching servers: **Ssh/** (connector, pool, jump hosts, tunnels, keys, LAN scan, remote exec), **Sessions/** (the transports behind terminal tabs: SSH shell, ConPTY local shells, serial, Telnet; profiles and session logs; `ITerminal`, the AI terminal bridge and the command risk rules), **Files/** (SFTP/SCP/local file systems, the transfer queue, folder sync), **Reports/** (server and fleet reports, log digest).

May reference: Dcx.Core. No WPF: terminal tabs are reached through `ITerminal` / `Terminals.Open`.
