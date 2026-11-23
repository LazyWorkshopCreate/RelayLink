package com.relaylink.sdk;

/**
 * 跨语言一致的错误类型，保留稳定 code 字符串。
 * 各语言实现的 code 必须完全一致，便于调用方统一处理。
 */
public class RelayLinkException extends RuntimeException {

    public static final String ENTRY_UNAVAILABLE = "entry_unavailable";
    public static final String ENTRY_NOT_FOUND = "entry_not_found";
    public static final String TUNNEL_REJECTED = "tunnel_rejected";
    public static final String TARGET_CONNECT_FAILED = "target_connect_failed";
    public static final String TIMEOUT = "timeout";
    public static final String CONFIGURATION_DRIFT = "configuration_drift";
    public static final String INVALID_USAGE = "invalid_usage";

    private final String code;

    public RelayLinkException(String code, String message) {
        super(message);
        this.code = code;
    }

    public RelayLinkException(String code, String message, Throwable cause) {
        super(message, cause);
        this.code = code;
    }

    public String code() {
        return code;
    }
}
