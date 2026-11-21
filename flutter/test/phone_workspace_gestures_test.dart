import 'package:flutter/widgets.dart';
import 'package:flutter_hbb/mobile/widgets/phone_workspace_gestures.dart';
import 'package:flutter_hbb/models/input_model.dart';
import 'package:flutter_hbb/models/model.dart';
import 'package:flutter_test/flutter_test.dart';

class _Model extends Fake implements FfiModel {
  @override
  final pi = PeerInfo();
  @override
  bool get keyboard => true;
  @override
  bool get viewOnly => false;
}

class _Cursor extends Fake implements CursorModel {
  @override
  bool shouldBlock(double x, double y) => false;
  @override
  bool isInRemoteRect(Offset point) => true;
  @override
  Future<bool> move(double x, double y) async => true;
}

class _Input extends Fake implements InputModel {
  final clicks = <MouseButtons>[];
  final pressed = <MouseButtons>{};
  @override
  Future<void> tap(MouseButtons b) async => clicks.add(b);
  @override
  Future<void> tapDown(MouseButtons b) async {
    pressed.add(b);
  }

  @override
  Future<void> tapUp(MouseButtons b) async {
    pressed.remove(b);
  }
}

class _Session extends Fake implements FFI {
  @override
  final ffiModel = _Model();
  @override
  final cursorModel = _Cursor();
  @override
  final inputModel = _Input();
}

void main() {
  testWidgets(
      'holding and releasing opens the menu, holding and moving selects',
      (tester) async {
    final session = _Session();
    session.ffiModel.pi
      ..phoneWorkspaceName = 'phone'
      ..displays.add(Display()..name = 'phone');
    await tester.pumpWidget(PhoneWorkspaceGestures(
        ffi: session, child: const ColoredBox(color: Color(0xff000000))));
    final point = tester.getCenter(find.byType(PhoneWorkspaceGestures));
    await tester.longPressAt(point);
    await tester.pump();
    expect(session.inputModel.clicks, [MouseButtons.right]);
    final touch = await tester.startGesture(point);
    await tester.pump(const Duration(milliseconds: 600));
    await touch.moveBy(const Offset(60, 0));
    await tester.pump();
    expect(session.inputModel.pressed, {MouseButtons.left});
    await touch.up();
    await tester.pump();
    expect(session.inputModel.pressed, isEmpty);
    expect(session.inputModel.clicks, [MouseButtons.right]);
  });

  test('the physical screen keeps its input mapping after display reordering',
      () {
    final phone = Display()..name = 'phone';
    final physical = Display()..name = 'physical';
    final pi = PeerInfo()..phoneWorkspaceName = 'phone';
    pi.displays.addAll([phone, physical]);
    expect(pi.isPhoneWorkspace, isTrue);
    pi.displays.value = [physical, phone];
    expect(pi.isPhoneWorkspace, isFalse);
    pi.currentDisplay = 1;
    expect(pi.isPhoneWorkspace, isTrue);
  });
}
