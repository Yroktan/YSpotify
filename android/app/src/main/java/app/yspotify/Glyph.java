package app.yspotify;

import android.animation.ValueAnimator;
import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.view.MotionEvent;
import android.view.View;

final class Glyph extends View {
    private final Paint paint = new Paint(3);
    final String role;
    private boolean playing, busy, active;
    private float angle;
    private ValueAnimator animation;
    Glyph(Context context, String role) {
        super(context); this.role = role; setClickable(true); setFocusable(true);
        GradientDrawable shape = new GradientDrawable(); shape.setShape(GradientDrawable.OVAL); shape.setColor(role.equals("play") ? 0xff91dba8 : 0x0023382b);
        setBackground(new RippleDrawable(ColorStateList.valueOf(0x5091dba8), shape, null)); setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_YES);
    }
    void playing(boolean value) { if (playing != value) { playing = value; invalidate(); } }
    void active(boolean value) { if (active != value) { active = value; invalidate(); } }
    void busy(boolean value) { if (busy != value) { busy = value; animateBusy(); invalidate(); } }
    private void animateBusy() {
        if (animation != null) { animation.cancel(); animation = null; }
        if (!busy || !isAttachedToWindow() || getWindowVisibility() != VISIBLE) return;
        animation = ValueAnimator.ofFloat(0, 360); animation.setDuration(1100); animation.setRepeatCount(ValueAnimator.INFINITE); animation.setInterpolator(new android.view.animation.LinearInterpolator()); animation.addUpdateListener(value -> { angle = (float) value.getAnimatedValue(); invalidate(); }); animation.start();
    }
    @Override protected void onAttachedToWindow() { super.onAttachedToWindow(); animateBusy(); }
    @Override protected void onDetachedFromWindow() { if (animation != null) { animation.cancel(); animation = null; } super.onDetachedFromWindow(); }
    @Override protected void onWindowVisibilityChanged(int value) { super.onWindowVisibilityChanged(value); animateBusy(); }
    @Override public boolean onTouchEvent(MotionEvent event) {
        if (isEnabled()) { if (event.getActionMasked() == MotionEvent.ACTION_DOWN) animate().scaleX(.9f).scaleY(.9f).setDuration(80).start(); else if (event.getActionMasked() == MotionEvent.ACTION_UP || event.getActionMasked() == MotionEvent.ACTION_CANCEL) animate().scaleX(1).scaleY(1).setDuration(120).start(); }
        return super.onTouchEvent(event);
    }
    @Override public void setEnabled(boolean value) { super.setEnabled(value); setAlpha(value ? 1 : .32f); }
    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas); canvas.save(); float size = Math.min(getWidth(), getHeight()); canvas.translate((getWidth() - size) / 2, (getHeight() - size) / 2); canvas.scale(size / 48, size / 48);
        paint.setColor(role.equals("play") ? 0xff132b1b : active ? 0xff91dba8 : 0xffbad0c1); paint.setStrokeWidth(2); paint.setStrokeCap(Paint.Cap.ROUND); paint.setStrokeJoin(Paint.Join.ROUND); paint.setStyle(Paint.Style.STROKE);
        if (busy) canvas.drawArc(new RectF(15, 15, 33, 33), angle, 260, false, paint);
        else if (role.equals("play") || role.equals("listen")) {
            paint.setStyle(Paint.Style.FILL);
            if (playing) { canvas.drawRoundRect(18, 16, 22, 32, 1, 1, paint); canvas.drawRoundRect(27, 16, 31, 32, 1, 1, paint); }
            else { Path path = new Path(); path.moveTo(20, 15); path.lineTo(20, 33); path.lineTo(33, 24); path.close(); canvas.drawPath(path, paint); }
        } else if (role.equals("next") || role.equals("previous")) {
            if (role.equals("previous")) { canvas.translate(48, 0); canvas.scale(-1, 1); }
            Path path = new Path(); path.moveTo(17, 16); path.lineTo(17, 32); path.lineTo(29, 24); path.close(); canvas.drawPath(path, paint); canvas.drawLine(32, 16, 32, 32, paint);
        } else if (role.equals("search")) { canvas.drawCircle(22, 21, 8, paint); canvas.drawLine(28, 27, 35, 34, paint); }
        else if (role.equals("heart")) { Path path = new Path(); path.moveTo(24, 34); path.cubicTo(7, 24, 11, 12, 19, 15); path.lineTo(24, 19); path.lineTo(29, 15); path.cubicTo(37, 12, 41, 24, 24, 34); canvas.drawPath(path, paint); }
        else if (role.equals("library")) { canvas.drawRoundRect(13, 14, 19, 35, 2, 2, paint); canvas.drawLine(24, 14, 24, 35, paint); canvas.drawLine(29, 15, 35, 34, paint); }
        else if (role.equals("back")) { canvas.drawLine(29, 15, 20, 24, paint); canvas.drawLine(20, 24, 29, 33, paint); }
        else if (role.equals("shuffle")) { Path path = new Path(); path.moveTo(13, 17); path.cubicTo(23, 17, 25, 31, 35, 31); path.moveTo(13, 31); path.cubicTo(23, 31, 25, 17, 35, 17); canvas.drawPath(path, paint); canvas.drawLine(31, 13, 35, 17, paint); canvas.drawLine(35, 17, 31, 21, paint); canvas.drawLine(31, 27, 35, 31, paint); }
        else if (role.equals("repeat")) { canvas.drawRoundRect(13, 16, 35, 32, 7, 7, paint); canvas.drawLine(29, 12, 35, 16, paint); canvas.drawLine(35, 16, 30, 21, paint); }
        else if (role.equals("menu")) { paint.setStyle(Paint.Style.FILL); canvas.drawCircle(24, 15, 1.7f, paint); canvas.drawCircle(24, 24, 1.7f, paint); canvas.drawCircle(24, 33, 1.7f, paint); }
        else if (role.equals("account")) { canvas.drawCircle(24, 19, 5, paint); canvas.drawArc(new RectF(14, 25, 34, 39), 190, 160, false, paint); }
        canvas.restore();
    }
}
