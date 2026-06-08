package com.atri.desktoppet;

public final class DesktopPetUnityEvents {
    private static final String RECEIVER = "AndroidNativeMusicBridge";
    private static final String UNITY_PLAYER_CLASS = "com.unity3d.player.UnityPlayer";

    private DesktopPetUnityEvents() {
    }

    public static void sendMusicStarted(String title) {
        send("OnAndroidMusicStarted", title);
    }

    public static void sendMusicCompleted() {
        send("OnAndroidMusicCompleted", "");
    }

    public static void sendMusicError(String error) {
        send("OnAndroidMusicError", error);
    }

    private static void send(String method, String payload) {
        try {
            Class<?> unityPlayer = Class.forName(UNITY_PLAYER_CLASS);
            unityPlayer
                    .getMethod("UnitySendMessage", String.class, String.class, String.class)
                    .invoke(null, RECEIVER, method, payload == null ? "" : payload);
        } catch (Throwable ignored) {
        }
    }
}
