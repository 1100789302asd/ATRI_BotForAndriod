package com.atri.desktoppet;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.graphics.PixelFormat;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowManager;

public final class DesktopPetOverlay {
    private static WindowManager windowManager;
    private static View overlayView;
    private static WindowManager.LayoutParams layoutParams;
    private static Activity unityActivity;
    private static float downRawX;
    private static float downRawY;
    private static int downX;
    private static int downY;
    private static long downTime;

    private DesktopPetOverlay() {
    }

    public static boolean canDrawOverlays(Activity activity) {
        if (activity == null) {
            return false;
        }

        return Build.VERSION.SDK_INT < Build.VERSION_CODES.M || Settings.canDrawOverlays(activity);
    }

    public static void requestPermission(Activity activity) {
        if (activity == null || canDrawOverlays(activity)) {
            return;
        }

        Intent intent = new Intent(
                Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                Uri.parse("package:" + activity.getPackageName()));
        activity.startActivity(intent);
    }

    public static void show(Activity activity, int sizeDp, int xDp, int yDp) {
        if (activity == null) {
            return;
        }

        unityActivity = activity;

        if (!canDrawOverlays(activity)) {
            requestPermission(activity);
            return;
        }

        if (overlayView != null) {
            return;
        }

        Context appContext = activity.getApplicationContext();
        windowManager = (WindowManager) appContext.getSystemService(Context.WINDOW_SERVICE);

        int sizePx = dp(appContext, sizeDp);
        int xPx = dp(appContext, xDp);
        int yPx = dp(appContext, yDp);

        overlayView = new View(appContext);
        GradientDrawable background = new GradientDrawable();
        background.setShape(GradientDrawable.OVAL);
        background.setColor(0xE633A6FF);
        overlayView.setBackground(background);

        int type = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                : WindowManager.LayoutParams.TYPE_PHONE;

        layoutParams = new WindowManager.LayoutParams(
                sizePx,
                sizePx,
                type,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                        | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL,
                PixelFormat.TRANSLUCENT);
        layoutParams.gravity = Gravity.TOP | Gravity.START;
        layoutParams.x = xPx;
        layoutParams.y = yPx;

        overlayView.setOnTouchListener(new View.OnTouchListener() {
            @Override
            public boolean onTouch(View view, MotionEvent event) {
                switch (event.getAction()) {
                    case MotionEvent.ACTION_DOWN:
                        downRawX = event.getRawX();
                        downRawY = event.getRawY();
                        downX = layoutParams.x;
                        downY = layoutParams.y;
                        downTime = System.currentTimeMillis();
                        return true;
                    case MotionEvent.ACTION_MOVE:
                        layoutParams.x = downX + (int) (event.getRawX() - downRawX);
                        layoutParams.y = downY + (int) (event.getRawY() - downRawY);
                        windowManager.updateViewLayout(overlayView, layoutParams);
                        return true;
                    case MotionEvent.ACTION_UP:
                    case MotionEvent.ACTION_CANCEL:
                        float distanceX = event.getRawX() - downRawX;
                        float distanceY = event.getRawY() - downRawY;
                        int tapSlop = dp(view.getContext(), 12);
                        boolean shortTap = System.currentTimeMillis() - downTime < 250;
                        boolean smallMove = distanceX * distanceX + distanceY * distanceY < tapSlop * tapSlop;
                        if (shortTap && smallMove) {
                            bringUnityToFront();
                        }
                        return true;
                    default:
                        return false;
                }
            }
        });

        windowManager.addView(overlayView, layoutParams);
    }

    public static void hide() {
        if (windowManager != null && overlayView != null) {
            windowManager.removeView(overlayView);
        }

        overlayView = null;
        layoutParams = null;
    }

    private static void bringUnityToFront() {
        if (unityActivity == null) {
            return;
        }

        Intent intent = unityActivity.getPackageManager().getLaunchIntentForPackage(unityActivity.getPackageName());
        if (intent == null) {
            return;
        }

        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        unityActivity.getApplicationContext().startActivity(intent);
    }

    private static int dp(Context context, int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }
}
