# A Public Website, Repository and Remote Build Coordinator for RenkuOSPorts

Build a project, targeting Azure Web App with a Blob Storage back end and an Azure Email Connector for notifications that recreates the HaikuPorts web server to serve up packages by platform ( x86, x86_64, ARM64 and RISC-V  for starters ). On the backend, use ASP Identity, with OAUTH for an orchestrator platform that a User can login, and setup a ‘Build Client’ that works with the recipes from HaikuPorts to build, and update the packages in the repo remotely.  Each upload should be tracked in a database ( Asume Azure SQL Server, with file, platform, build date, upload dates, recipe updated data and a the identity of the user the built it ).  


Use vue.js, and .net10 for the infrastructure. 

Each build client should be a command line app that logs in to the orchestrator and gets the next scheduled recipe for its platform, and is tasked with building it.  download should mark as in progress, a failure should clear, log and notify ( as well as be shown on the Ports dashboard that the administrators can see ), a success should upload, mark as complete and be put into the public facing repo. packages should be stored in the BLOB storage. 

Go ahead and set up the build clients much like you set up the script from RenkuOSPorts project. Have no idea if this will work, but it should and would allow spreading the build load while enabling reliable scalable hosting
