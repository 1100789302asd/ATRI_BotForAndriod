package com.atri.desktoppet;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.MediaPlayer;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

import java.io.IOException;

public final class DesktopPetMusicService extends Service {
    public static final String ACTION_PLAY = "com.atri.desktoppet.action.PLAY";
    public static final String ACTION_PAUSE = "com.atri.desktoppet.action.PAUSE";
    public static final String ACTION_RESUME = "com.atri.desktoppet.action.RESUME";
    public static final String ACTION_STOP = "com.atri.desktoppet.action.STOP";
    public static final String EXTRA_PATH = "path";
    public static final String EXTRA_TITLE = "title";

    private static final String TAG = "DesktopPetMusic";
    private static final String CHANNEL_ID = "desktop_pet_music";
    private static final int NOTIFICATION_ID = 1007;
    private static MediaPlayer player;
    private static String currentTitle = "";

    public static boolean isPlaying() {
        try {
            return player != null && player.isPlaying();
        } catch (IllegalStateException exception) {
            return false;
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || intent.getAction() == null) {
            return START_STICKY;
        }

        String action = intent.getAction();
        if (ACTION_PLAY.equals(action)) {
            play(intent.getStringExtra(EXTRA_PATH), intent.getStringExtra(EXTRA_TITLE));
        } else if (ACTION_PAUSE.equals(action)) {
            pause();
        } else if (ACTION_RESUME.equals(action)) {
            resume();
        } else if (ACTION_STOP.equals(action)) {
            stopPlayback();
            stopForeground(true);
            stopSelf();
        }

        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void play(String path, String title) {
        if (path == null || path.length() == 0) {
            Log.w(TAG, "play skipped: empty path");
            return;
        }

        currentTitle = title != null && title.length() > 0 ? title : "ATRI Music";
        startForeground(NOTIFICATION_ID, buildNotification(currentTitle));
        stopPlayback();

        player = new MediaPlayer();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            player.setAudioAttributes(new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build());
        }

        try {
            player.setDataSource(path);
            player.setOnCompletionListener(mp -> {
                DesktopPetUnityEvents.sendMusicCompleted();
                stopPlayback();
            });
            player.prepare();
            player.start();
            DesktopPetUnityEvents.sendMusicStarted(currentTitle);
        } catch (IOException | RuntimeException exception) {
            Log.w(TAG, "play failed", exception);
            DesktopPetUnityEvents.sendMusicError(exception.toString());
            stopPlayback();
            stopForeground(true);
        }
    }

    private void pause() {
        if (player != null && player.isPlaying()) {
            player.pause();
        }
    }

    private void resume() {
        if (player != null && !player.isPlaying()) {
            startForeground(NOTIFICATION_ID, buildNotification(currentTitle));
            player.start();
        }
    }

    private void stopPlayback() {
        if (player == null) {
            return;
        }

        try {
            player.stop();
        } catch (RuntimeException ignored) {
        }

        player.release();
        player = null;
    }

    private Notification buildNotification(String title) {
        createChannel();

        Intent launchIntent = getPackageManager().getLaunchIntentForPackage(getPackageName());
        PendingIntent pendingIntent = null;
        if (launchIntent != null) {
            int flags = Build.VERSION.SDK_INT >= Build.VERSION_CODES.M
                    ? PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
                    : PendingIntent.FLAG_UPDATE_CURRENT;
            pendingIntent = PendingIntent.getActivity(this, 0, launchIntent, flags);
        }

        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        builder.setContentTitle(title != null && title.length() > 0 ? title : "ATRI Music")
                .setContentText("Playing in background")
                .setSmallIcon(getApplicationInfo().icon)
                .setOngoing(true);

        if (pendingIntent != null) {
            builder.setContentIntent(pendingIntent);
        }

        return builder.build();
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }

        NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (manager == null || manager.getNotificationChannel(CHANNEL_ID) != null) {
            return;
        }

        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID,
                "Desktop Pet Music",
                NotificationManager.IMPORTANCE_LOW);
        manager.createNotificationChannel(channel);
    }
}
