import 'dart:convert';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter_hbb/models/input_model.dart';
import 'package:flutter_hbb/models/model.dart';
import 'package:flutter_hbb/models/platform_model.dart';

class PhoneWorkspaceGestures extends StatefulWidget {
  final FFI ffi;
  final Widget child;
  const PhoneWorkspaceGestures(
      {super.key, required this.ffi, required this.child});

  @override
  State<PhoneWorkspaceGestures> createState() => _PhoneWorkspaceGesturesState();
}

class _PhoneWorkspaceGesturesState extends State<PhoneWorkspaceGestures>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker = createTicker(_coast);
  late ClampingScrollSimulation _fling;
  Offset _flingDirection = Offset.zero;
  double _lastDistance = 0;
  Offset _remainder = Offset.zero;
  bool _stopTap = false;
  bool _scrolling = false;
  bool _pinching = false;
  double _scale = 1;
  Offset? _hold;
  Offset? _heldScrollPoint;
  bool _selecting = false;
  Future<void> _selection = Future.value();

  bool get _enabled =>
      mounted &&
      widget.ffi.ffiModel.pi.isPhoneWorkspace &&
      widget.ffi.ffiModel.keyboard &&
      !widget.ffi.ffiModel.viewOnly;
  bool _inside(Offset point) =>
      _enabled &&
      !widget.ffi.cursorModel.shouldBlock(point.dx, point.dy) &&
      widget.ffi.cursorModel.isInRemoteRect(point);

  Future<void> _tap(Offset point, MouseButtons button) async {
    if (_inside(point) &&
        await widget.ffi.cursorModel.move(point.dx, point.dy) &&
        _enabled) await widget.ffi.inputModel.tap(button);
  }

  void _scroll(Offset delta) {
    if (!_enabled) return;
    final scale = widget.ffi.canvasModel.scale;
    if (!scale.isFinite || scale <= 0) return;
    // Win32 high-resolution wheel units: 120 per notch. Chromium's default
    // notch scrolls 100 pixels at 100% scaling. Convert the phone viewport
    // to remote pixels first, just as we do when positioning the cursor.
    _remainder += delta * (120 / 100 / scale);
    final x = _remainder.dx.truncate(), y = _remainder.dy.truncate();
    _remainder -= Offset(x.toDouble(), y.toDouble());
    if (x == 0 && y == 0) return;
    bind.sessionSendMouse(
        sessionId: widget.ffi.sessionId,
        msg: jsonEncode({
          'type': 'trackpad',
          'x': x.toString(),
          'y': y.toString(),
        }));
  }

  void _coast(Duration elapsed) {
    final t = elapsed.inMicroseconds / Duration.microsecondsPerSecond;
    if (!_enabled) {
      _ticker.stop();
      return;
    }
    final distance = _fling.x(t);
    _scroll(_flingDirection * (distance - _lastDistance));
    _lastDistance = distance;
    if (_fling.isDone(t)) _ticker.stop();
  }

  void _moveAfterHold(LongPressMoveUpdateDetails event) {
    final origin = _hold;
    if (origin == null || !_inside(event.localPosition)) return;
    final offset = event.localPosition - origin;
    if (!_selecting && _heldScrollPoint == null) {
      if (offset.distance < kTouchSlop) return;
      if (offset.dy.abs() > offset.dx.abs()) {
        widget.ffi.cursorModel.move(origin.dx, origin.dy);
        _remainder = Offset.zero;
        _heldScrollPoint = origin;
      }
    }
    final previous = _heldScrollPoint;
    if (previous != null) {
      _scroll(event.localPosition - previous);
      _heldScrollPoint = event.localPosition;
      return;
    }
    final first = !_selecting;
    _selecting = true;
    final ffi = widget.ffi;
    _selection = _selection.then((_) async {
      if (!_enabled) return;
      if (first) {
        if (!await ffi.cursorModel.move(origin.dx, origin.dy) || !_enabled)
          return;
        await ffi.inputModel.tapDown(MouseButtons.left);
      }
      if (_enabled)
        await ffi.cursorModel
            .move(event.localPosition.dx, event.localPosition.dy);
    });
  }

  void _endSelection() {
    if (_selecting) {
      final input = widget.ffi.inputModel;
      _selection =
          _selection.whenComplete(() => input.tapUp(MouseButtons.left));
    }
    _selecting = false;
    _hold = null;
    _heldScrollPoint = null;
  }

  @override
  void dispose() {
    _endSelection();
    _ticker.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Listener(
        onPointerDown: (_) {
          _stopTap = _ticker.isActive;
          _ticker.stop();
        },
        onPointerCancel: (_) => _endSelection(),
        child: GestureDetector(
          supportedDevices: const {
            PointerDeviceKind.touch,
            PointerDeviceKind.stylus
          },
          onTapUp: (d) {
            if (!_stopTap) _tap(d.localPosition, MouseButtons.left);
          },
          onLongPressStart: (d) {
            _hold = _inside(d.localPosition) ? d.localPosition : null;
          },
          onLongPressMoveUpdate: _moveAfterHold,
          onLongPressEnd: (_) {
            final point = _hold;
            if (!_selecting && _heldScrollPoint == null && point != null) {
              _tap(point, MouseButtons.right);
            }
            _endSelection();
          },
          onScaleStart: (d) {
            _scrolling = _inside(d.localFocalPoint);
            _pinching = d.pointerCount > 1;
            _scale = 1;
            _remainder = Offset.zero;
            if (_scrolling)
              widget.ffi.cursorModel
                  .move(d.localFocalPoint.dx, d.localFocalPoint.dy);
          },
          onScaleUpdate: (d) {
            if (!_scrolling || !_enabled) return;
            if (d.pointerCount > 1) {
              _pinching = true;
              final canvas = widget.ffi.canvasModel;
              if (!canvas.locked) {
                canvas.updateScale(d.scale / _scale, d.focalPoint);
                canvas.panX(d.focalPointDelta.dx);
                canvas.panY(d.focalPointDelta.dy);
              }
              _scale = d.scale;
            } else if (!_pinching) {
              _scroll(d.focalPointDelta);
            }
          },
          onScaleEnd: (d) {
            final velocity = d.velocity.pixelsPerSecond;
            if (_scrolling &&
                !_pinching &&
                velocity.distance > kMinFlingVelocity) {
              _flingDirection = velocity / velocity.distance;
              _fling = ClampingScrollSimulation(
                  position: 0,
                  velocity: velocity.distance.clamp(0, kMaxFlingVelocity).toDouble());
              _lastDistance = 0;
              _ticker.start();
            }
            _scrolling = false;
          },
          child: widget.child,
        ),
      );
}
