package com.relaylink.sdk;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;

/**
 * JUnit 5 entrypoint delegating to {@link ConformanceRunner} so both Maven Surefire
 * and dependency-free `javac`/`java` runners execute the full suite.
 */
class ConformanceTest {

    @Test
    void fullConformanceSuitePasses() {
        assertDoesNotThrow(ConformanceRunner::runAll);
    }
}
