import org.apache.logging.log4j.LogManager;
import org.apache.logging.log4j.Logger;

// ...

public class YunUsers {
    private static final Logger logger = LogManager.getLogger(YunUsers.class);

    // ...

    public void someMethod() {
        // Log messages at different levels
        logger.trace("This is a TRACE message");
        logger.debug("This is a DEBUG message");
        logger.info("This is an INFO message");
        logger.warn("This is a WARN message");
        logger.error("This is an ERROR message");
        logger.fatal("This is a FATAL message");
    }

    // ...
}
