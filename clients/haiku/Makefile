## RenkuPorts Builder, Haiku makefile-engine Makefile. Build with `make`; the binary lands in objects.<arch>-<cc>-release/
## (or OBJ_DIR=...). `make install` copies it to INSTALL_DIR.

NAME = RenkuPortsBuilder
TYPE = APP
APP_MIME_SIG = application/x-vnd.RenkuOS-PortsBuilder
SRCS = Main.cpp Worker.cpp ApiClient.cpp Settings.cpp
RDEFS = Builder.rdef
RSRCS =
# libnetservices.a: the legacy HTTP kit (BHttpRequest); libshared.a: BJson.
LIBS = be bnetapi netservices shared $(STDCPPLIBS)
LIBPATHS =
SYSTEM_INCLUDE_PATHS = $(shell findpaths -e B_FIND_PATH_HEADERS_DIRECTORY private/netservices) \
	$(shell findpaths -e B_FIND_PATH_HEADERS_DIRECTORY private/shared)
LOCAL_INCLUDE_PATHS =
OPTIMIZE := FULL
LOCALES =
DEFINES =
WARNINGS = ALL
SYMBOLS :=
DEBUGGER :=
COMPILER_FLAGS =
LINKER_FLAGS =
APP_VERSION :=
INSTALL_DIR = $(shell finddir B_USER_NONPACKAGED_DIRECTORY)/apps
DRIVER_PATH =

DEVEL_DIRECTORY := \
	$(shell findpaths -r "makefile_engine" B_FIND_PATH_DEVELOP_DIRECTORY)
include $(DEVEL_DIRECTORY)/etc/makefile-engine
