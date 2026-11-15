import importlib.util
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('observer',Path(__file__).with_name('observer.py'))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

class Clock:
    def __init__(self):self.value=0
    def now(self):return self.value
    def sleep(self,seconds):self.value+=seconds

class ObservationWindowTests(unittest.TestCase):
    def test_unavailable_sampler_keeps_the_full_receipt_window_and_records_failure(self):
        clock=Clock();reads=[]
        def read():reads.append(clock.now());return [{'id':'accepted','event':'completed'}]
        def sample(_):raise ConnectionRefusedError('reproduced metrics failure')
        errors=m.observe_window(180,read,lambda row:None,sample,clock=clock.now,sleep=clock.sleep)
        self.assertEqual(180,clock.now());self.assertEqual(180,len(reads))
        self.assertTrue(errors['sampling']);self.assertFalse(errors['receipts']);self.assertFalse(errors['integrity'])

    def test_receipt_failure_and_corruption_remain_failed_without_stopping_later_observations(self):
        clock=Clock();validated=[]
        def read():
            if clock.now()<2:raise OSError('temporary journal read failure')
            return [{'id':'accepted'},{'id':'corrupt'}]
        def validate(row):
            if row['id']=='corrupt':raise AssertionError('checksum differs')
            validated.append(row['id'])
        errors=m.observe_window(10,read,validate,lambda deadline:None,clock=clock.now,sleep=clock.sleep)
        self.assertEqual(10,clock.now());self.assertEqual(8,len(validated))
        self.assertEqual(2,len(errors['receipts']));self.assertEqual(1,len(errors['integrity']))
        self.assertFalse(errors['sampling'])

if __name__=='__main__':unittest.main()
