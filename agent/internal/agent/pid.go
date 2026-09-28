package agent

import "os"

func osPid() int { return os.Getpid() }
